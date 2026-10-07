/**
 * Creates the success envelope expected by Steam service callers.
 * @param body Response body returned by Body().toObject().
 * @returns An object exposing BSuccess/BFailed/GetEResult/Body.
 */
const transportReply = (body: object) => ({
    BSuccess: () => true,
    BFailed: () => false,
    GetEResult: () => 1,
    Body: () => ({...body, toObject: () => body}),
});

/**
 * Creates a refused Steam service response with EResult 2.
 * @param body Failure response body.
 * @returns The same transport shape as transportReply, marked failed.
 */
const transportFailure = (body: object) => ({
    ...transportReply(body),
    BSuccess: () => false,
    BFailed: () => true,
    GetEResult: () => 2,
});

// Replacing a stub is only half the job: react-query still holds the answer the stub gave, so the
// UI keeps rendering the refusal until the query that cached it is invalidated.
//
// The client has one query client, built by the module that provides it with its default options
// and mounts the devtools beside it. Client builds renumber modules and rename exports, so it is
// found by that provider's source and by the shape of the client.
//
// Failure is swallowed on purpose. A client whose query layer moved keeps the stale answer and the
// row simply does not update — which is a degraded surface, not a broken one, and never a reason to
// tear down a gate that is otherwise working.
const QueryClientTokens = ["ReactQueryDevtools", "offlineFirst"];
const isQueryClient = (value: any) =>
    typeof value?.invalidateQueries === "function" && typeof value?.getQueryState === "function";
/**
 * Resolves Steam's query client without exposing factory or export identities to callers.
 * @param req Shared module resolver.
 * @returns The uniquely shaped query client, or null on discovery/load failure.
 */
const resolveQueryClient = (req: any) => {
    try {
        return req?.exported(QueryClientTokens, isQueryClient) ?? null;
    } catch {
        return null;
    }
};
/**
 * Invalidates a Steam query after a supplied service reply changes.
 * @param req Shared module resolver.
 * @param queryKey Query key to invalidate.
 */
const invalidateQuery = (req: any, queryKey: unknown) => {
    try {
        resolveQueryClient(req)?.invalidateQueries({queryKey});
    } catch {
        // Intentionally ignored; see above.
    }
};
