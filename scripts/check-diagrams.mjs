#!/usr/bin/env node
/**
 * check-diagrams.mjs — verify that every Mermaid diagram exists in exactly two
 * places and that the two agree.
 *
 * Why two places at all:
 *
 *   `docs/DIAGRAMS.md` must carry the diagram source inline, because that is the
 *   only form GitHub renders. `docs/diagrams/*.mmd` must exist separately,
 *   because the LaTeX editions include them as rendered PDFs — a paper cannot
 *   read a fenced code block — and because a diagram nobody can open on its own
 *   is a diagram nobody reuses in a slide, an issue or a review comment.
 *
 *   Two copies of anything is a drift surface, and this one has already been
 *   sitting undefended: README.md embeds the architecture diagram and
 *   `architecture.mmd` holds the same bytes, with nothing checking that they
 *   stay the same. The answer to a drift surface is never "remember to update
 *   both"; it is a check that fails.
 *
 * Three rules:
 *
 *   R1  A section headed `### A1. …` in docs/DIAGRAMS.md owns the file whose
 *       name starts `a1-`, and the two must be byte-identical. The id is the
 *       join key, which leaves the filename free to describe the diagram.
 *   R2  Every `.mmd` under docs/diagrams/ must be claimed by some section —
 *       an unreferenced diagram is one the reader never sees.
 *   R3  Every ```mermaid block in README.md must be byte-identical to some
 *       tracked `.mmd` file. The README is not a numbered section so it cannot
 *       join on an id, but copying a diagram into it and then editing one of
 *       the two copies is the same defect.
 *
 * Zero dependencies on purpose: this runs before anything has been installed.
 *
 * Usage: node scripts/check-diagrams.mjs
 * Exit:  0 = every diagram is paired and identical; 1 = at least one is not.
 */

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';

const repoRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const docPath = join(repoRoot, 'docs', 'DIAGRAMS.md');
const dirPath = join(repoRoot, 'docs', 'diagrams');
const readmePath = join(repoRoot, 'README.md');

/** Tracked `.mmd` files outside docs/diagrams/ that a README block may match. */
const LOOSE_SOURCES = ['architecture.mmd'];

if (!existsSync(docPath)) {
  console.error('check-diagrams: docs/DIAGRAMS.md is missing. It is the rendered half of this pair.');
  process.exit(1);
}

if (!existsSync(dirPath)) {
  console.error('check-diagrams: docs/diagrams/ is missing. It is the reusable half of this pair.');
  process.exit(1);
}

const markdown = readFileSync(docPath, 'utf8');

/**
 * Sections and the Mermaid block each one owns. A section heading looks like
 * `### A1. System context — who emits, who collects`; the id is `A1`.
 */
const sections = [];
{
  let current = null;
  let fence = null;

  for (const line of markdown.split('\n')) {
    const heading = line.match(/^###\s+([A-D]\d+)\.\s+(.+)$/);

    if (heading) {
      current = { id: heading[1].toLowerCase(), title: heading[2].trim(), source: null };
      sections.push(current);
      continue;
    }

    if (fence === null && line.trim() === '```mermaid') {
      fence = [];
      continue;
    }

    if (fence !== null && line.trim() === '```') {
      if (current && current.source === null) {
        current.source = `${fence.join('\n')}\n`;
      }
      fence = null;
      continue;
    }

    if (fence !== null) {
      fence.push(line);
    }
  }
}

if (sections.length === 0) {
  console.error('check-diagrams: docs/DIAGRAMS.md declares no `### A1.`-style sections. That cannot be right.');
  process.exit(1);
}

const files = readdirSync(dirPath).filter((name) => name.endsWith('.mmd'));
const failures = [];
const claimed = new Set();

// ---- R1: each section owns exactly one file, and they agree ----------------

for (const section of sections) {
  const id = section.id.toUpperCase();

  if (section.source === null) {
    failures.push(`Section ${id} ("${section.title}") has no \`\`\`mermaid block.`);
    continue;
  }

  const matches = files.filter((name) => name.startsWith(`${section.id}-`));

  if (matches.length === 0) {
    failures.push(
      `Section ${id} ("${section.title}") has no file in docs/diagrams/. `
      + `Expected one named ${section.id}-<slug>.mmd.`,
    );
    continue;
  }

  if (matches.length > 1) {
    failures.push(`Section ${id} matches more than one file: ${matches.join(', ')}.`);
    continue;
  }

  const [name] = matches;
  claimed.add(name);

  if (readFileSync(join(dirPath, name), 'utf8') !== section.source) {
    failures.push(
      `docs/diagrams/${name} and section ${id} of docs/DIAGRAMS.md have drifted. `
      + 'Whichever you edited, copy it to the other — they are the same diagram.',
    );
  }
}

// ---- R2: no orphan files ---------------------------------------------------

for (const name of files) {
  if (!claimed.has(name)) {
    failures.push(
      `docs/diagrams/${name} is not shown by any section of docs/DIAGRAMS.md. `
      + 'Add a section for it, or delete the file — a diagram no document shows is one nobody reads.',
    );
  }
}

// ---- R3: README blocks match a tracked source ------------------------------

if (existsSync(readmePath)) {
  const known = new Map();

  for (const name of files) {
    known.set(`docs/diagrams/${name}`, readFileSync(join(dirPath, name), 'utf8'));
  }

  for (const name of LOOSE_SOURCES) {
    const absolute = join(repoRoot, name);
    if (existsSync(absolute)) known.set(name, readFileSync(absolute, 'utf8'));
  }

  const readme = readFileSync(readmePath, 'utf8');
  const blocks = [...readme.matchAll(/```mermaid\n([\s\S]*?)```/g)].map((m) => m[1]);

  blocks.forEach((block, index) => {
    const hit = [...known.entries()].find(([, source]) => source === block);

    if (!hit) {
      failures.push(
        `README.md mermaid block #${index + 1} matches no tracked .mmd file. `
        + `It must be byte-identical to one of: ${[...known.keys()].join(', ')}.`,
      );
    }
  });
}

if (failures.length > 0) {
  console.error('check-diagrams: FAILED\n');
  for (const failure of failures) console.error(`  - ${failure}`);
  console.error('\nA diagram that disagrees with itself is worse than no diagram: the reader trusts it.');
  process.exit(1);
}

console.log(
  `check-diagrams: ${sections.length} section(s), ${files.length} file(s), README blocks accounted for — all identical.`,
);
