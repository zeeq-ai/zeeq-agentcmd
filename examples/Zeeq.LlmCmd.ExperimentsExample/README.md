# Runtime experiments example

Run the example from the repository root:

```sh
mise run demo:experiments
```

The example uses synthetic chat clients. It does not need credentials or a network connection. JSON reflection is disabled.

It writes a publication to a temporary file and calls a generated invoice command. The control variant returns to the caller; the candidate runs in a separate shadow scope. The host reads the shadow by its exact stored output reference. The example then writes a second publication, waits for the short demo cache lifetime, and calls the same command again. The candidate now supplies the prompt, the opted-in locale, the client, the model, and the temperature.

The example deletes the temporary file on exit. The cache lifetime is short to make the update visible in the demo; production hosts choose an appropriate lifetime. The in-memory scheduler and ledger do not survive a restart.
