using Zeeq.AgentCmd;

namespace CommandRoot;

public sealed partial class PromptTemplateCommand : PromptCommand<InvoiceParameters, Invoice>
{
    const string Id = "invoice.prompt-template";
    const string Profile = "extraction";
    const string UserTemplate = "{Document}";
    const string SystemTemplate = "Use {Locale}";
}
