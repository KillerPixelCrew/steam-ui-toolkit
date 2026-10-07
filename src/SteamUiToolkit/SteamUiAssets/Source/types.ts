/** Host-supplied immutable bridge configuration; replaced only by installing another generation. */
type BridgeConfiguration = Readonly<{
    /** Wire protocol version expected by both endpoints. */
    version: number;
    /** Private window property owning this bridge generation. */
    namespace: string;
    /** CDP Runtime binding used for page-to-host envelopes. */
    binding: string;
    /** Hash of the complete injected asset, used to reject stale bridge reuse. */
    assetHash: string;
    /** Revision of the fixed patch command allowlist. */
    vocabularyRevision: string;
    /** Current SharedJSContext execution generation. */
    contextGeneration: number;
    /** Current document generation within that context. */
    documentGeneration: number;
    /** Maximum concurrent unresolved page-to-host requests. */
    maximumPending: number;
    /** Per-request timeout; expiry sends best-effort cancellation and rejects locally. */
    timeoutMilliseconds: number;
    /** Patch ids mapped to the exact command names that the page may send. */
    allowed: Readonly<Record<string, readonly string[]>>;
}>;

declare const __STEAM_UI_CONFIGURATION_JSON__: BridgeConfiguration;

// This file is a script, not a module, so the interface merges with the global
// Window directly; a `declare global` block would need a module context.
interface Window {
    /** Steam webpack chunk registry; shared runtime capture uses this instead of hard-coded module ids. */
    webpackChunksteamui: unknown[];

    [key: string]: any;
}
