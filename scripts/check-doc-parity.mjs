#!/usr/bin/env node
/**
 * check-doc-parity.mjs — the bilingual documents come in pairs, and neither half
 * may be edited without the other.
 *
 * Why this exists:
 *
 *   Most of this repository is English only. A named set of documents — the
 *   tutorials and the two LaTeX editions of the manual — is deliberately
 *   bilingual, because the people who read it and the people who review it do
 *   not read the same language.
 *
 *   Two copies of a document is a drift surface, and the failure mode is
 *   specific: somebody fixes a wrong command in the English tutorial, the Polish
 *   one keeps telling its reader to run the wrong thing, and nothing goes red.
 *   That is worse than having no translation, because the reader trusts it.
 *   This repository has already shipped that exact defect in one language: the
 *   dashboard's own documentation page told Claude Code users to set two
 *   variables and omitted the one without which nothing is exported at all.
 *
 *   So there are two rules, and the second is the one that matters:
 *
 *     R1  STRUCTURAL — every bilingual document has both halves.
 *     R2  COUPLING   — a commit that edits one half must edit the other.
 *
 *   R2 cannot check that a translation is *correct*; no script can. It checks
 *   that somebody looked. A convention about remembering is a convention that
 *   fails on the busy week.
 *
 * R2 only runs when a base ref is given, because it needs a diff. CI passes
 * origin/master; locally you can pass anything `git diff` understands, or omit
 * it and get R1 alone.
 *
 * Zero dependencies: this runs before anything has been installed.
 *
 * Usage: node scripts/check-doc-parity.mjs [base-ref]
 * Exit:  0 = paired, and coupled if a base ref was given; 1 = not.
 */

import { readdirSync, existsSync, statSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';

const repoRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();
const baseRef = process.argv[2];

/**
 * Directories that are bilingual in full: every `*.md` in one must have a
 * `*.pl.md` twin. Adding a directory here is how you opt a new area in.
 */
const BILINGUAL_DIRS = [
  'docs/tutorials',
];

/**
 * Individual pairs that are not a whole directory, given as
 * [english, polish]. The LaTeX editions are the reason this list exists: they
 * pair on `.pl.tex`, not `.pl.md`.
 */
const BILINGUAL_PAIRS = [
  ['docs/papers/copilotscope-manual.tex', 'docs/papers/copilotscope-manual.pl.tex'],
];

const PL = '.pl.md';

/** Returns [english, polish] for every bilingual document. */
function pairs() {
  const found = [];

  for (const dir of BILINGUAL_DIRS) {
    const absolute = join(repoRoot, dir);

    if (!existsSync(absolute) || !statSync(absolute).isDirectory()) {
      console.error(`check-doc-parity: '${dir}' is declared bilingual but does not exist.`);
      process.exit(1);
    }

    for (const name of readdirSync(absolute)) {
      if (name.endsWith('.md') && !name.endsWith(PL)) {
        const english = `${dir}/${name}`;
        found.push([english, `${english.slice(0, -'.md'.length)}${PL}`]);
      }
    }
  }

  for (const [english, polish] of BILINGUAL_PAIRS) {
    if (found.some(([e]) => e === english)) continue; // already covered by its directory
    found.push([english, polish]);
  }

  return found.sort(([a], [b]) => a.localeCompare(b));
}

const failures = [];
const documents = pairs();

if (documents.length === 0) {
  console.error('check-doc-parity: no bilingual documents found. That cannot be right — failing loudly.');
  process.exit(1);
}

// ---- R1: both halves exist -------------------------------------------------

for (const [english, polish] of documents) {
  if (!existsSync(join(repoRoot, english))) {
    failures.push(`${english} is declared bilingual but does not exist.`);
    continue;
  }

  if (!existsSync(join(repoRoot, polish))) {
    failures.push(`${english} has no Polish half. Expected ${polish}.`);
  }
}

// A stray `.pl.md` with no English original is the same defect, mirrored.
for (const dir of BILINGUAL_DIRS) {
  for (const name of readdirSync(join(repoRoot, dir))) {
    if (!name.endsWith(PL)) continue;

    const englishName = `${name.slice(0, -PL.length)}.md`;

    if (!existsSync(join(repoRoot, dir, englishName))) {
      failures.push(`${dir}/${name} has no English original. Expected ${dir}/${englishName}.`);
    }
  }
}

// ---- R2: neither half moves alone ------------------------------------------

if (baseRef) {
  let changed;

  try {
    changed = new Set(
      execFileSync('git', ['diff', '--name-only', `${baseRef}...HEAD`], { encoding: 'utf8', cwd: repoRoot })
        .split('\n')
        .map((line) => line.trim())
        .filter(Boolean),
    );
  } catch {
    // No merge base — a fork with no shared history, or a shallow clone that
    // does not reach it. Structural parity still held, and reporting a coupling
    // failure nobody can act on would train people to ignore this check.
    console.log(`check-doc-parity: no merge base with ${baseRef} — structural parity only.`);
    changed = null;
  }

  if (changed) {
    for (const [english, polish] of documents) {
      const movedEnglish = changed.has(english);
      const movedPolish = changed.has(polish);

      if (movedEnglish && !movedPolish) {
        failures.push(`${english} changed but ${polish} did not. Update the translation, or say in the pull request why it did not need it.`);
      }

      if (movedPolish && !movedEnglish) {
        failures.push(`${polish} changed but ${english} did not. Update the English, or say in the pull request why it did not need it.`);
      }
    }
  }
}

if (failures.length > 0) {
  console.error('check-doc-parity: FAILED\n');
  for (const failure of failures) console.error(`  - ${failure}`);
  console.error('\nA translation that drifts is worse than no translation: the reader trusts it.');
  process.exit(1);
}

console.log(
  `check-doc-parity: ${documents.length} bilingual document(s), both halves present`
  + `${baseRef ? ' and neither edited alone' : ''}.`,
);
