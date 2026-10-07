// Durable ownership claims shared across CDP evaluations. Restore exact descriptors and inherited
// membership; never stack wrappers, replace a real backend or spoof global platform identity.

const defineHidden = (host: object, key: string, value: unknown) => {
    Object.defineProperty(host, key, {
        value,
        configurable: true,
        enumerable: false,
        writable: false,
    });
};

/** Durable property names shared by every evaluation that owns the same claim. */
type ClaimKeys = {
    /** Boolean ownership marker stored on the claimed object. */
    readonly marker: string;
    /** Hidden property retaining the displaced member or descriptor snapshot. */
    readonly original: string;
};

/** Successful acquisition identifies reclamation; refusal carries a diagnostic and leaves other owners intact. */
type ClaimOutcome = { ok: true; reclaimed: boolean } | { ok: false; error: string };

/** Exact property ownership and descriptor needed to restore inherited versus own membership. */
type PropertySnapshot = Readonly<{
    kind: "steam-ui-property-snapshot-v1";
    hadOwn: boolean;
    descriptor?: PropertyDescriptor;
    value: unknown;
}>;

const claimed = (host: unknown, keys: ClaimKeys) =>
    !!host && (host as Record<string, unknown>)[keys.marker] === true;

// What a claim stored as the displaced original.
const storedOriginal = (host: unknown, keys: ClaimKeys): unknown => {
    const record = host as Record<string, unknown>;
    return Object.hasOwn(record, keys.original) ? record[keys.original] : undefined;
};

// Removes a claim's markers, so the next probe does not read a released claim as one.
const dropClaimKeys = (host: Record<string, unknown>, keys: ClaimKeys) => {
    delete host[keys.marker];
    delete host[keys.original];
};

const captureProperty = (host: Record<string, unknown>, property: string): PropertySnapshot => ({
    kind: "steam-ui-property-snapshot-v1",
    hadOwn: Object.hasOwn(host, property),
    descriptor: Object.getOwnPropertyDescriptor(host, property),
    value: host[property],
});

const isPropertySnapshot = (value: unknown): value is PropertySnapshot =>
    !!value &&
    typeof value === "object" &&
    (value as Partial<PropertySnapshot>).kind === "steam-ui-property-snapshot-v1" &&
    typeof (value as Partial<PropertySnapshot>).hadOwn === "boolean";

// What a claimed member displaced, or the value itself when it is not ours. For code that has to
// recognise a component by its source while the gate may already hold it: a probe or a re-resolve
// that tests the live value sees the wrapper, and a wrapper carries none of the original's tokens.
const unclaimedValue = (value: unknown, keys: ClaimKeys): unknown => {
    if (!claimed(value, keys)) return value;
    const stored = storedOriginal(value, keys);
    return isPropertySnapshot(stored) ? stored.value : stored;
};

// An accessor-backed field is one whose value lives BEHIND the property — a MobX observable, a
// store's computed flag — and the only safe way to change it is through its own setter.
// Redefining or deleting the accessor destroys the store's bookkeeping while leaving the getter in
// place: Steam's settings message (a MobX object) then throws
// `Cannot read properties of undefined (reading 'get')` on every later read, which crashed the
// Quick Access Menu until the client restarted (device-reproduced 2026-09-01, brightness flag).
const accessorSetter = (host: object, property: string) => {
    const current = Object.getOwnPropertyDescriptor(host, property);
    if (!current || "value" in current) return null;
    return typeof current.set === "function" ? current.set : undefined;
};

const restoreProperty = (
    host: Record<string, unknown>,
    property: string,
    snapshot: PropertySnapshot,
) => {
    const setter = accessorSetter(host, property);
    if (setter !== null) {
        if (setter === undefined) {
            throw new TypeError("restore target is a read-only accessor");
        }
        if (host[property] !== snapshot.value) host[property] = snapshot.value;
        return;
    }
    if (snapshot.hadOwn && snapshot.descriptor) {
        Object.defineProperty(host, property, snapshot.descriptor);
    } else {
        delete host[property];
    }
};

const installDataValue = (
    host: Record<string, unknown>,
    property: string,
    value: unknown,
) => {
    const descriptor = Object.getOwnPropertyDescriptor(host, property);
    if (descriptor) {
        if (!("value" in descriptor)) {
            // Through the setter, never by redefinition — see accessorSetter. Read back because a
            // setter is free to ignore the write, and a claim that did not take must not be marked.
            if (typeof descriptor.set !== "function") {
                throw new TypeError("claim target is a read-only accessor");
            }
            host[property] = value;
            if (host[property] !== value) {
                throw new TypeError("claim target did not accept the value");
            }
            return;
        }
        Object.defineProperty(host, property, {...descriptor, value});
    } else {
        Object.defineProperty(host, property, {
            value,
            configurable: true,
            enumerable: true,
            writable: true,
        });
    }
};

/**
 * Claims a field while preserving the exact underlying property across bridge replacements.
 * @param host Object containing the field, or null when unavailable.
 * @param field Existing field to replace.
 * @param keys Stable marker/original property names shared by later evaluations.
 * @param next Desired value; an unowned field already equal to it is refused.
 * @returns Claim status and reclaim flag, or a diagnostic; failed installation attempts rollback.
 */
const claimValue = (
    host: Record<string, unknown> | null,
    field: string,
    keys: ClaimKeys,
    next: unknown,
): ClaimOutcome => {
    if (!host || !(field in host)) {
        return {ok: false, error: "claim target unavailable"};
    }
    const reclaimed = claimed(host, keys);
    // Already at the target value and NOT marked means the client did this itself. Refusing is
    // correct: there is nothing to add, and restoring later would hand back a value we invented.
    if (!reclaimed && host[field] === next) {
        return {ok: false, error: "already set by the client"};
    }
    const fieldBefore = captureProperty(host, field);
    const markerBefore = Object.getOwnPropertyDescriptor(host, keys.marker);
    const originalBefore = Object.getOwnPropertyDescriptor(host, keys.original);
    try {
        const original = reclaimed ? storedOriginal(host, keys) : fieldBefore;
        installDataValue(host, field, next);
        defineHidden(host, keys.marker, true);
        defineHidden(host, keys.original, original);
        return {ok: true, reclaimed};
    } catch (error) {
        try {
            restoreProperty(host, field, fieldBefore);
            if (markerBefore) Object.defineProperty(host, keys.marker, markerBefore);
            else delete host[keys.marker];
            if (originalBefore) Object.defineProperty(host, keys.original, originalBefore);
            else delete host[keys.original];
        } catch {
            // The primary error remains the useful diagnosis; a hostile Proxy can also refuse rollback.
        }
        return {ok: false, error: String(error)};
    }
};

/**
 * Restores a claimed field through its saved descriptor or original accessor setter.
 * @param host Claimed object, or null.
 * @param field Field named at installation.
 * @param keys The same durable keys used to claim the field.
 * @returns Success for an absent claim; otherwise restoration status and any diagnostic.
 */
const releaseValue = (
    host: Record<string, unknown> | null,
    field: string,
    keys: ClaimKeys,
): { ok: boolean; error?: string } => {
    if (!host || !claimed(host, keys)) return {ok: true};
    try {
        // A claim whose stored original is not a snapshot restores nothing; saying so keeps the
        // caller from forgetting a claim that is still in place.
        const original = storedOriginal(host, keys);
        if (!isPropertySnapshot(original)) return {ok: false, error: "stored original invalid"};
        restoreProperty(host, field, original);
        dropClaimKeys(host, keys);
        return {ok: true};
    } catch (error) {
        return {ok: false, error: String(error)};
    }
};

/**
 * Replaces a member with a marked object/function without stacking prior toolkit wrappers.
 * @param host Object whose member is replaced, or null.
 * @param member Member to claim.
 * @param keys Durable marker/original keys on the replacement.
 * @param replacement Builds the replacement from the underlying original value.
 * @returns Claim status; a replacement that cannot carry its marker is refused.
 */
const claimMember = (
    host: Record<string, unknown> | null,
    member: string,
    keys: ClaimKeys,
    replacement: (original: unknown) => unknown,
): ClaimOutcome => {
    if (!host) {
        return {ok: false, error: "claim host unavailable"};
    }
    const current = host[member];
    const reclaimed = claimed(current, keys);
    try {
        const original = reclaimed
            ? (storedOriginal(current, keys) as PropertySnapshot)
            : captureProperty(host, member);
        const next = replacement(original.value) as Record<string, unknown>;
        // Functions as well as objects: every member claim so far replaces a METHOD, and `typeof` a
        // function is "function", not "object". Excluding it left the replacement unmarked, so the
        // release found nothing of ours and handed nothing back — the overlay outlived its own
        // removal.
        if (!next || (typeof next !== "object" && typeof next !== "function")) {
            return {ok: false, error: "claim replacement cannot carry its marker"};
        }
        defineHidden(next, keys.marker, true);
        defineHidden(next, keys.original, original);
        installDataValue(host, member, next);
        return {ok: true, reclaimed};
    } catch (error) {
        return {ok: false, error: String(error)};
    }
};

/**
 * Restores only the currently marked member, including its prior inherited/absent state.
 * @param host Object carrying the replacement, or null.
 * @param member Claimed member name.
 * @param keys Marker/original keys from installation.
 * @returns Restoration status; an absent or no-longer-owned member is already released.
 */
const releaseMember = (
    host: Record<string, unknown> | null,
    member: string,
    keys: ClaimKeys,
): { ok: boolean; error?: string } => {
    if (!host) return {ok: true};
    const current = host[member];
    if (!claimed(current, keys)) return {ok: true};
    try {
        restoreProperty(host, member, storedOriginal(current, keys) as PropertySnapshot);
        return {ok: true};
    } catch (error) {
        return {ok: false, error: String(error)};
    }
};

/**
 * Checks the live member for this claim marker.
 * @param host Object to inspect, or null/undefined.
 * @param member Member name.
 * @param keys Claim marker/original keys.
 * @returns Whether the current member is marked as owned.
 */
const memberClaimed = (
    host: Record<string, unknown> | null | undefined,
    member: string,
    keys: ClaimKeys,
) => claimed(host?.[member], keys);

/**
 * Supplies an absent namespace or reclaims an orphaned toolkit namespace.
 * @param host Namespace parent, or null.
 * @param name Property receiving the namespace.
 * @param marker Durable ownership marker.
 * @param factory Builds the supplied object; no real unmarked backend is replaced.
 * @returns Claim/reclaim status or the refusal reason.
 */
const supplyNamespace = (
    host: Record<string, unknown> | null,
    name: string,
    marker: string,
    factory: () => object,
): ClaimOutcome => {
    if (!host) {
        return {ok: false, error: "namespace host unavailable"};
    }
    const current = host[name];
    if (current && !claimed(current, {marker, original: marker})) {
        return {ok: false, error: `${name} already exists`};
    }
    try {
        const api = factory();
        defineHidden(api, marker, true);
        Object.defineProperty(host, name, {
            value: api,
            configurable: true,
            enumerable: true,
            writable: false,
        });
        return {ok: true, reclaimed: !!current};
    } catch (error) {
        return {ok: false, error: String(error)};
    }
};

/**
 * Deletes only the namespace carrying the supplied marker.
 * @param host Namespace parent, or null/undefined.
 * @param name Namespace property name.
 * @param marker Marker used when supplying the namespace.
 * @returns Removal status; missing or foreign namespaces are left intact.
 */
const withdrawNamespace = (
    host: Record<string, unknown> | null | undefined,
    name: string,
    marker: string,
): { ok: boolean; error?: string } => {
    if (!host || !claimed(host[name], {marker, original: marker})) return {ok: true};
    try {
        delete host[name];
        return {ok: true};
    } catch (error) {
        return {ok: false, error: String(error)};
    }
};

/**
 * Claims a configurable own accessor while retaining its complete original descriptor.
 * @param host Object that defines the accessor, not an inheriting instance.
 * @param property Own property to replace.
 * @param keys Durable marker/original keys carried by the getter.
 * @param getter Replacement getter.
 * @returns Claim/reclaim status; unavailable or non-configurable properties are refused.
 */
const claimAccessor = (
    host: object | null,
    property: string,
    keys: ClaimKeys,
    getter: () => unknown,
): ClaimOutcome => {
    if (!host) {
        return {ok: false, error: "claim host unavailable"};
    }
    const descriptor = Object.getOwnPropertyDescriptor(host, property);
    if (!descriptor || descriptor.configurable !== true) {
        return {ok: false, error: "property is not configurable"};
    }
    try {
        const reclaimed = claimed(descriptor.get, keys);
        const original = reclaimed ? storedOriginal(descriptor.get, keys) : descriptor;
        defineHidden(getter, keys.marker, true);
        defineHidden(getter, keys.original, original);
        Object.defineProperty(host, property, {get: getter, configurable: true});
        return {ok: true, reclaimed};
    } catch (error) {
        return {ok: false, error: String(error)};
    }
};

/**
 * Restores the descriptor saved by a still-owned accessor claim.
 * @param host Object defining the accessor, or null.
 * @param property Claimed property.
 * @param keys Marker/original keys from installation.
 * @returns Restoration status; missing saved originals remain failures.
 */
const releaseAccessor = (
    host: object | null,
    property: string,
    keys: ClaimKeys,
): { ok: boolean; error?: string } => {
    if (!host) return {ok: true};
    try {
        const descriptor = Object.getOwnPropertyDescriptor(host, property);
        if (!claimed(descriptor?.get, keys)) return {ok: true};
        const original = storedOriginal(descriptor!.get, keys);
        // Our getter with nothing to hand back is still installed: not a success.
        if (!original) return {ok: false, error: "accessor original missing"};
        Object.defineProperty(host, property, original as PropertyDescriptor);
        return {ok: true};
    } catch (error) {
        return {ok: false, error: String(error)};
    }
};

// One claim on a set of function members that several surfaces transform: taken with the first
// transform and released with the last, because two wrappers on one member would each hand back the
// other's wrapper or the original from under it on removal. `wrap` builds the replacement around the
// displaced original and reads the live transforms at call time. The claim's marker and original
// live on the wrapper, so a bridge replaced in place reclaims rather than wraps its predecessor.
//
// The wrappers sit on paths Steam calls for every element and every memo, so they read the
// transforms as a plain array kept in registration order, rebuilt only when one is added or
// withdrawn, and loop over it by index: a Map iterator per call was garbage on every render.
const createSharedClaim = <Transform>(
    keys: ClaimKeys,
    members: readonly string[],
    unavailable: string,
    uninstallable: string,
    wrap: (original: any, transforms: () => readonly Transform[]) => unknown,
) => {
    const transforms = new Map<string, Transform>();
    let active: readonly Transform[] = Object.freeze([]);
    const refresh = () => {
        active = Object.freeze([...transforms.values()]);
    };
    const activeTransforms = () => active;
    let wrappers: Record<string, unknown> | null = null;
    const holds = (host: Record<string, unknown>) => {
        const current = wrappers;
        return !!current && members.every((member) => host[member] === current[member]);
    };

    const intercept = (
        host: Record<string, unknown> | null | undefined,
        name: string,
        transform: Transform,
    ): { ok: boolean; error?: string } => {
        if (!host || members.some((member) => typeof host[member] !== "function")) {
            return {ok: false, error: unavailable};
        }
        transforms.set(name, transform);
        refresh();
        if (holds(host)) return {ok: true};
        const installed: Record<string, unknown> = {};
        for (const member of members) {
            const claim = claimMember(host, member, keys, (original) => wrap(original, activeTransforms));
            if (!claim.ok || !memberClaimed(host, member, keys)) {
                for (const done of Object.keys(installed)) releaseMember(host, done, keys);
                transforms.delete(name);
                refresh();
                return {ok: false, error: claim.ok ? uninstallable : claim.error};
            }
            installed[member] = host[member];
        }
        wrappers = installed;
        return {ok: true};
    };

    // Withdraws one transform, and hands the members back once none is left. Without a host the
    // wrappers stay installed, so that is reported and the caller releases again with one.
    const release = (
        host: Record<string, unknown> | null | undefined,
        name: string,
    ): { ok: boolean; error?: string } => {
        transforms.delete(name);
        refresh();
        if (transforms.size) return {ok: true};
        if (!host) return wrappers ? {ok: false, error: "host unavailable"} : {ok: true};
        for (const member of members) {
            const released = releaseMember(host, member, keys);
            if (!released.ok) return released;
        }
        wrappers = null;
        return {ok: true};
    };

    const intercepted = (host: Record<string, unknown> | null | undefined, name: string) =>
        !!host && transforms.has(name) && holds(host);

    return {intercept, release, intercepted};
};

// Intercepts what React.useMemo returns, for every surface that needs to see an array Steam builds
// through it: the Quick Access tab list, the Settings page list. React has one useMemo, so this is
// one shared claim. Transforms run in registration order, each seeing the result of the one before,
// and one that throws leaves the value as it found it.
const memoClaim = createSharedClaim<(value: unknown) => unknown>(
    {marker: "__steamUiOwnedUseMemo", original: "__steamUiOriginalUseMemo"},
    ["useMemo"],
    "React useMemo unavailable",
    "React useMemo wrapper could not be installed",
    (original, transforms) =>
        function SteamUiUseMemo(factory, dependencies) {
            let value = original(factory, dependencies);
            const list = transforms();
            for (let index = 0; index < list.length; index++) {
                try {
                    value = list[index](value);
                } catch {
                    // A failing transform leaves what it was given.
                }
            }
            return value;
        },
);
/**
 * Registers a named transform on the shared React.useMemo claim; throwing transforms preserve the previous result.
 * @param host Steam React or JSX runtime object.
 * @param name Unique transform identity for later release.
 * @param transform Transformation callback; it must not recursively call the claimed wrappers.
 * @returns Installation status; transforms run in registration order.
 */
const interceptMemo = memoClaim.intercept;
/**
 * Withdraws one transform and restores the claimed functions when the last transform leaves.
 * @param host Original React/JSX host; required while a wrapper remains installed.
 * @param name Registered transform identity.
 * @returns Cleanup status; unavailable hosts with remaining wrappers are reported as failures.
 */
const releaseMemo = memoClaim.release;
/**
 * Checks that a named transform and all corresponding live wrappers are still owned.
 * @param host React/JSX host to inspect.
 * @param name Transform identity.
 * @returns Whether both registration and wrapper ownership hold.
 */
const memoIntercepted = memoClaim.intercepted;

/**
 * Transforms an element before React renders it; first non-undefined result wins.
 * @param create Original JSX constructor, bypassing all registered transforms.
 * @param type Element component or intrinsic type; check cheaply before inspecting props.
 * @param props Original element props.
 * @param key React element key.
 * @returns A replacement element, or undefined to continue. Throwing transforms are skipped.
 */
type ElementTransform = (
    create: (...args: unknown[]) => unknown,
    type: unknown,
    props: any,
    key: unknown,
) => unknown;
const elementClaim = createSharedClaim<ElementTransform>(
    {marker: "__steamUiOwnedElements", original: "__steamUiOriginalElements"},
    ["jsx", "jsxs"],
    "JSX runtime unavailable",
    "JSX runtime wrapper could not be installed",
    (original, transforms) =>
        function SteamUiElement(this: unknown, type, props, key) {
            const list = transforms();
            for (let index = 0; index < list.length; index++) {
                try {
                    const replaced = list[index](original, type, props, key);
                    if (replaced !== undefined) return replaced;
                } catch {
                    // A failing transform leaves the element to the runtime.
                }
            }
            return original.apply(this, arguments as any);
        },
);
/**
 * Registers a named JSX transform on the shared jsx/jsxs claims; the first defined result wins.
 * @param host Steam React or JSX runtime object.
 * @param name Unique transform identity for later release.
 * @param transform Transformation callback; it must not recursively call the claimed wrappers.
 * @returns Installation status; throwing transforms are skipped.
 */
const interceptElements = elementClaim.intercept;
/**
 * Withdraws one transform and restores the claimed functions when the last transform leaves.
 * @param host Original React/JSX host; required while a wrapper remains installed.
 * @param name Registered transform identity.
 * @returns Cleanup status; unavailable hosts with remaining wrappers are reported as failures.
 */
const releaseElements = elementClaim.release;
/**
 * Checks that a named transform and all corresponding live wrappers are still owned.
 * @param host React/JSX host to inspect.
 * @param name Transform identity.
 * @returns Whether both registration and wrapper ownership hold.
 */
const elementsIntercepted = elementClaim.intercepted;
