import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { CONVERSION_PROMPT } from './conversionPrompt';

/**
 * ST-123: the manual path must ship the SAME prompt the server job uses.
 * docs/prompts/chord-page-to-chordpro.md is the single source of truth (the job's system
 * prompt, the Dockerfile's COPY, the docs-site page); this test fails the moment the
 * embedded copy drifts from the fenced body of that file.
 */
describe('conversionPrompt', () => {
  it('matches the fenced prompt body in docs/prompts/chord-page-to-chordpro.md', () => {
    const md = readFileSync(
      fileURLToPath(new URL('../../../docs/prompts/chord-page-to-chordpro.md', import.meta.url)),
      'utf8',
    );
    const fenced = md.match(/```\n([\s\S]*?)\n```/);
    expect(fenced, 'the doc must contain the fenced prompt body').not.toBeNull();
    expect(CONVERSION_PROMPT).toBe(fenced![1]);
  });
});
