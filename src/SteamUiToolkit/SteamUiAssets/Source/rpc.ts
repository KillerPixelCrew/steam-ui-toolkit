// Answering what Steam asks.
//
// The client calls a service method and reads a transport reply, not a bare value. Two gates
// answer such calls — the SteamOS Manager's GetState and the Bluetooth service's stubs — and both
// had built the same reply shape and the same query invalidation by hand.
//
// Overlaying the method itself is an ownership claim (claimMember); what is here is the rest of
// the job, which is the half that is easy to forget.

// The shape Steam reads back from a service call. BSuccess decides whether the caller proceeds at
// all, so a reply that omits it is discarded before its body is ever looked at; Body().toObject()
// is what the store then consumes.
const transportReply = (body: object) => ({
  BSuccess: () => true,
  BFailed: () => false,
  GetEResult: () => 1,
  Body: () => ({ ...body, toObject: () => body }),
});

// Replacing a stub is only half the job: react-query still holds the answer the stub gave, so the
// UI keeps rendering the refusal until the query that cached it is invalidated.
//
// The client has one query client, built by the module that provides it with its default options
// and mounts the devtools beside it. It was module 21371, export L, when first verified; the
// September 2026 beta renumbered the module, so it is found by that provider's source and by the
// shape of the client instead.
//
// Failure is swallowed on purpose. A client whose query layer moved keeps the stale answer and the
// row simply does not update — which is a degraded surface, not a broken one, and never a reason to
// tear down a gate that is otherwise working.
// The same conjunction the storage gate resolves its query client by.
const QueryClientTokens = ["ReactQueryDevtools", "offlineFirst"];
const isQueryClient = (value: any) =>
  typeof value?.invalidateQueries === "function" && typeof value?.getQueryState === "function";
const invalidateQuery = (req: any, queryKey: unknown) => {
  try {
    req?.exported(QueryClientTokens, isQueryClient).invalidateQueries({ queryKey });
  } catch {
    // Intentionally ignored; see above.
  }
};
