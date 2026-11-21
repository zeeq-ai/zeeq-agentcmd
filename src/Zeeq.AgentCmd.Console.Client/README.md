# Management console client

Run `mise exec -- pnpm --dir src/Zeeq.AgentCmd.Console.Client dev` from the repository root. Vite proxies `/api` to port 5180 by default; set `AGENTCMD_API_PROXY` to change the target. The client sends scope headers and browser credentials through its central API boundary.

The checked-in `openapi.json` is the generation input. With the intended API host running, refresh it with `mise exec -- pnpm --dir src/Zeeq.AgentCmd.Console.Client openapi:snapshot`. Set `AGENTCMD_OPENAPI_URL` to use another host. Run `mise exec -- pnpm --dir src/Zeeq.AgentCmd.Console.Client generate` to regenerate TypeScript models and fetch clients in `src/generated`. Review the snapshot and generated changes together. Generation is offline after the snapshot is captured.

The authored API boundary retains scope, credentials, cancellation, revision headers and closed error outcomes. It wraps the generated envelope type; module bodies retain authored types while the server exposes them as `JsonElement`. Kubb emits JSON integers as `number`, matching the transport; JavaScript cannot represent integers beyond its safe range exactly. Generated sources are excluded from lint because Kubb owns their formatting.
