---
title: Zeeq LlmCmd
description: Typed prompt commands over Microsoft.Extensions.AI. Keep intent, parameters, execution, and stored-output evaluation together.
seo:
  title: Zeeq LlmCmd — Prompts as typed commands
  description: A SqlCommand-inspired command layer over Microsoft.Extensions.AI. Stable prompt identity, captured parameters, closed outcomes, and evaluation of the exact stored whole.
---

::u-page-hero{class="dark:bg-gradient-to-b from-neutral-900 to-neutral-950"}
---
orientation: horizontal
---
#top
:hero-background

#title
Prompts as [typed commands]{.brand-gradient-text}.

#description
Inspired by `SqlCommand`, built on Microsoft.Extensions.AI. Keep prompt intent, parameters, execution, and results together — from one extraction to a whole you can evaluate.

#links
  :::u-button
  ---
  to: /getting-started/quick-start
  size: xl
  trailing-icon: i-lucide-arrow-right
  ---
  Get started
  :::

  :::u-button
  ---
  to: https://github.com/zeeq-ai/zeeq-llmcmd
  target: _blank
  size: xl
  variant: outline
  color: neutral
  icon: i-simple-icons-github
  ---
  GitHub
  :::

#default
  :::hero-code
  ```csharp [ExtractInvoice.cs] {13-15}
  // Keep identity and intent together.
  // The generator supplies the contract.
  public sealed partial class ExtractInvoice
      : PromptCommand<InvoiceInput, Invoice>
  {
      const string Id = "invoice.extract";
      const string UserTemplate =
          "Extract the invoice from {Document}.";
  }

  // Build a value. ExecuteAsync runs work.
  var result = await extractInvoice
      .WithParameters(new InvoiceInput(document))
      .ExecuteAsync(ct);
  // result is Outcome<Invoice>.
  ```
  :::
::

::u-page-section
#headline
Why LlmCmd?

#title
The prompt is one part of the operation.

#description
MEAI already gives you typed responses, schema helpers, and middleware. LlmCmd adds a place to keep stable prompt identity, captured inputs, delivery receipts, and evaluation of stored results.

#default
  :::u-page-grid{class="lg:grid-cols-2"}
    ::::u-page-card
    ---
    title: Start with MEAI
    icon: i-lucide-plug
    ---
    Keep a direct `IChatClient` call when it meets your needs. Your host chooses the provider and composes retry, caching, tools, and telemetry through MEAI.
    ::::

    ::::u-page-card
    ---
    title: Add a command when identity matters
    icon: i-lucide-fingerprint
    ---
    Give a repeated operation an ID and a typed parameter contract. Inspect the raw template and captured inputs. Handle a closed outcome and check whether the output was stored.
    ::::
  :::
::

::u-page-section{class="bg-muted/40"}
#title
Keep the important choices visible.

#features
  :::u-page-feature
  ---
  title: Stable prompt identity
  icon: i-lucide-fingerprint
  description: A class rename should not change which prompt ran. Keep an explicit ID beside the template and its parameter types.
  ---
  :::

  :::u-page-feature
  ---
  title: Generated contracts
  icon: i-lucide-braces
  description: Avoid hand-maintained schema and serializer registrations for supported DTOs. The generator emits metadata; unsupported shapes get a diagnostic.
  ---
  :::

  :::u-page-feature
  ---
  title: Immutable invocations
  icon: i-lucide-git-branch
  description: Try another profile or branch a plan without mutating the original. Only ExecuteAsync starts work.
  ---
  :::

  :::u-page-feature
  ---
  title: Closed outcomes
  icon: i-lucide-circle-check
  description: Handle Success or Failed directly, then inspect the delivery receipt. A successful value does not by itself promise persistence.
  ---
  :::

  :::u-page-feature
  ---
  title: Your MEAI pipeline
  icon: i-lucide-workflow
  description: Bring standard IChatClient implementations and host middleware. Provider accounting and telemetry stay optional and separate from the command layer.
  ---
  :::

  :::u-page-feature
  ---
  title: Evaluate the whole
  icon: i-lucide-clipboard-check
  description: Compose typed steps, keep their lineage, and read back the exact stored aggregate. Grade the final invoice without grading each extraction separately.
  ---
  :::
::

::u-page-section
#title
From intent to an inspectable result.

#description
The command describes the operation. MEAI runs the provider call. A stored output reference identifies what you can read back and grade.

#default
:call-flow
::

::u-page-section{class="bg-muted/40"}
#title
Start with a use case.

#default
  :::u-page-grid{class="lg:grid-cols-2"}
    ::::u-page-card
    ---
    title: Extract an invoice
    description: Run an offline typed command, then inspect its outcome.
    icon: i-lucide-receipt
    to: /examples/invoice
    ---
    ::::

    ::::u-page-card
    ---
    title: Assemble and grade a whole
    description: Use several extraction steps and evaluate the stored aggregate.
    icon: i-lucide-layers
    to: /examples/composition
    ---
    ::::

    ::::u-page-card
    ---
    title: Compare model profiles
    description: Keep client pipelines, model choices, and trial identities explicit.
    icon: i-lucide-sliders-horizontal
    to: /examples/model-profiles
    ---
    ::::

    ::::u-page-card
    ---
    title: Classify a support request
    description: Explore a small contract and deliberate failure handling.
    icon: i-lucide-messages-square
    to: /examples/support-classification
    ---
    ::::
  :::
::

::u-page-section
  :::u-page-c-t-a
  ---
  title: Write one command.
  description: Run the quick start with a fake client. No API key, paid call, or container is needed. This is a local alpha on .NET 11 / C# 15 RC; packages are built from source.
  links:
    - label: Run the quick start
      to: /getting-started/quick-start
      trailingIcon: i-lucide-arrow-right
    - label: Understand the design
      to: /getting-started/introduction
      variant: outline
      color: neutral
  ---
  :::
::
