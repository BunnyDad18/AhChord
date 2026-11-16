import { defineConfig } from 'vitepress'
import { fileURLToPath } from 'node:url'
import { cpSync } from 'node:fs'
import { join } from 'node:path'

const screenshots = fileURLToPath(new URL('../../../docs/screenshots', import.meta.url))
const dist = fileURLToPath(new URL('./dist', import.meta.url))

// The front-page hero and the favicon are served from the site root; copy them out of
// docs/screenshots (the single source) into dist as-is.
const copySiteRootImages = {
  name: 'ahchord-copy-root-images',
  apply: 'build' as const,
  closeBundle() {
    for (const f of ['player-setlist.png', 'chord-diagram.png']) {
      cpSync(join(screenshots, f), join(dist, f))
    }
  },
}

// Anchors exactly as GitHub's markdown renders them ("Backup & restore" -> #backup--restore,
// "Configuration (.env)" -> #configuration-env), so the #anchors the docs use across files
// resolve on the site too.
const githubSlug = (s: string) =>
  s
    .normalize('NFKD')
    .replace(/[^\w\s-]/g, '')
    .trim()
    .toLowerCase()
    .replace(/\s/g, '-')

const item = (text: string, link: string) => ({ text, link })

export default defineConfig({
  // Project page on GitHub Pages: https://bunnydad18.github.io/AhChord/
  base: '/AhChord/',
  // The site is built from the repo itself: index.md (front page) + SECURITY.md at the root,
  // the user guide, deployment guide, screenshots and prompts under docs/.
  srcDir: '../../',
  // The front-page hero + favicon are served from the site root (see the copy plugin).
  // Root-level pages (index.md, SECURITY.md) import bare 'vue' / 'vue/server-renderer'
  // (compiled vnodes) — their importers sit at the repo root, OUTSIDE docs/site/node_modules,
  // so both specifiers are aliased to the site's own vue (ST-129: a cold-cache build fails
  // without this; the dev box's warm dep cache hid it since ST-114).
  vite: {
    resolve: {
      alias: [
        { find: /^vue$/, replacement: fileURLToPath(new URL('../node_modules/vue/dist/vue.runtime.esm-bundler.js', import.meta.url)) },
        { find: /^vue\/server-renderer$/, replacement: fileURLToPath(new URL('../node_modules/@vue/server-renderer/dist/server-renderer.esm-bundler.js', import.meta.url)) },
      ],
    },
    plugins: [copySiteRootImages],
  },
  cleanUrls: true,
  lastUpdated: true,
  title: 'AhChord',
  description:
    'Self-hosted chord chart manager & player — user guide, deployment guide and screenshots.',
  head: [['link', { rel: 'icon', type: 'image/png', href: '/AhChord/chord-diagram.png' }]],
  // Development docs (task queue, gate rules, engineering journal…) are excluded here and
  // stripped from the published tree (scripts/publish-github.sh) — they never reach the site.
  srcExclude: [
    '**/node_modules/**',
    '**/.vitepress/**',
    'README.md',
    'CHANGELOG.md',
    'CONTRIBUTING.md',
    'AGENTS.md',
    'Mobile/README.md',
    'Frontend/**/*.md',
    'WebApi/**/*.md',
    'WebApi.Tests/**/*.md',
    'docs/CODE_RULES.md',
    'docs/ROADMAP.md',
    'docs/TECHNICAL_ARCHITECTURE.md',
    'docs/DESIGN_AND_USER_EXPERIENCE.md',
    'docs/E2E_CHECKLIST.md',
    'docs/MOBILE_PLAN.md',
    'docs/PERF.md',
    'docs/CHANGELOG-journal.md',
    'docs/CHANGELOG-archive.md',
    'docs/tasks/**',
    'docs/mockups/**',
  ],
  markdown: {
    anchor: { level: [2, 3], slugify: githubSlug },
  },
  themeConfig: {
    nav: [
      { text: 'User guide', link: '/docs/user/' },
      { text: 'Deployment', link: '/docs/DEPLOYMENT' },
      { text: 'Screenshots', link: '/docs/screenshots/README' },
      { text: 'GitHub', link: 'https://github.com/BunnyDad18/AhChord' },
    ],
    sidebar: {
      '/docs/user/': [
        {
          text: 'User guide',
          items: [
            item('Start here', '/docs/user/'),
            item('Getting started', '/docs/user/getting-started'),
            item('Library & songs', '/docs/user/library-and-songs'),
            item('The player', '/docs/user/player'),
            item('Setlists & sessions', '/docs/user/setlists-and-sessions'),
            item('Import', '/docs/user/import'),
            item('Mobile & offline', '/docs/user/mobile-and-offline'),
            item('Settings', '/docs/user/settings'),
            item('Shortcuts & FAQ', '/docs/user/shortcuts-and-faq'),
            item('ChordPro basics', '/docs/user/chordpro-basics'),
          ],
        },
      ],
      '/docs/': [
        {
          text: 'Self-hosting',
          items: [
            item('Deployment guide', '/docs/DEPLOYMENT'),
            item('Screenshots', '/docs/screenshots/README'),
            item('Chord page → ChordPro prompt', '/docs/prompts/chord-page-to-chordpro'),
            item('Metadata-review prompt', '/docs/prompts/metadata-review'),
            item('Security', '/SECURITY'),
          ],
        },
      ],
      '/': [
        {
          text: 'Self-hosting',
          items: [
            item('Deployment guide', '/docs/DEPLOYMENT'),
            item('Screenshots', '/docs/screenshots/README'),
            item('Chord page → ChordPro prompt', '/docs/prompts/chord-page-to-chordpro'),
            item('Metadata-review prompt', '/docs/prompts/metadata-review'),
            item('Security', '/SECURITY'),
          ],
        },
      ],
    },
    socialLinks: [{ icon: 'github', link: 'https://github.com/BunnyDad18/AhChord' }],
    footer: {
      message: 'Released under the MIT License.',
      copyright: 'AhChord — ChordPro charts, self-hosted.',
    },
    search: { provider: 'local' },
  },
})
