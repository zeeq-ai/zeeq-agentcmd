export default defineAppConfig({
  ui: {
    colors: { primary: 'zeeq', neutral: 'zinc' },
    prose: {
      codeIcon: { 'nuget.config': 'i-lucide-file-code' },
      code: { base: 'max-w-full whitespace-normal [overflow-wrap:anywhere]' }
    },
    pageHero: { slots: { container: 'min-w-0 py-12 sm:py-16 lg:py-20 gap-10 sm:gap-y-12', wrapper: 'min-w-0' } },
    pageSection: { slots: { headline: 'text-center text-primary' } },
    footer: { slots: { root: 'border-t border-default', left: 'text-sm text-muted' } }
  },
  seo: { siteName: 'Zeeq AgentCmd' },
  header: {
    title: '',
    to: '/',
    logo: { alt: '', light: '', dark: '' },
    search: true,
    colorMode: true,
    links: [{
      'icon': 'i-simple-icons-github',
      'to': 'https://github.com/zeeq-ai/zeeq-llmcmd',
      'target': '_blank',
      'aria-label': 'AgentCmd on GitHub'
    }]
  },
  footer: {
    credits: 'Zeeq AgentCmd · Built on Microsoft.Extensions.AI',
    colorMode: false,
    links: [{
      label: 'MIT License',
      to: 'https://github.com/zeeq-ai/zeeq-llmcmd/blob/main/LICENSE',
      target: '_blank'
    }, {
      'icon': 'i-simple-icons-github',
      'to': 'https://github.com/zeeq-ai/zeeq-llmcmd',
      'target': '_blank',
      'aria-label': 'AgentCmd on GitHub'
    }, {
      'icon': 'i-lucide-file-text',
      'to': '/llms.txt',
      'aria-label': 'Documentation for language models'
    }]
  },
  toc: {
    title: 'On this page',
    bottom: {
      title: 'Links',
      edit: 'https://github.com/zeeq-ai/zeeq-llmcmd/edit/main/docs/content',
      links: [{
        icon: 'i-lucide-code',
        label: 'Runnable examples',
        to: 'https://github.com/zeeq-ai/zeeq-llmcmd/tree/main/examples',
        target: '_blank'
      }]
    }
  }
})
