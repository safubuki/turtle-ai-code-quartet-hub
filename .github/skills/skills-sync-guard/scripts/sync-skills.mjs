#!/usr/bin/env node
// Bidirectional skill sync engine for turtle-skills-neo.
//
// Reconciles the three per-agent skill directories so that all of them hold the
// same content, regardless of which one you edited:
//
//   .agents/skills  -> OpenAI Codex (also read by GitHub Copilot)
//   .claude/skills  -> Claude Code
//   .github/skills  -> GitHub Copilot
//
// This is the runner invoked by skills-sync-guard
// (.agents/skills/skills-sync-guard/scripts/safe-sync-skills.mjs), but it can
// also be run directly. Pure Node + POSIX/Windows safe paths, so it works the
// same on Windows and Ubuntu and travels with the repo through git.
//
// Strategies:
//   --strategy latest  For each file, the newest version (by mtime) wins and is
//                      copied into every directory. Never deletes. Safe default
//                      when directories have diverged ("edit anywhere").
//   --strategy base    Treat --base as the source of truth and make the other
//                      directories match it. With deletion enabled (default),
//                      files missing from base are removed from the others.
//
// Usage:
//   node scripts/sync-skills.mjs [--strategy latest|base] [--base agents|claude|github]
//                                [--dry-run] [--no-delete] [--verbose]
//                                [--no-backup] [--backup-dir <path>]   (accepted, no-op here)

import { mkdir, readdir, readFile, rm, copyFile, stat, utimes } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';

// Keep in sync with TARGETS in skills-sync-guard/scripts/safe-sync-skills.mjs.
const TARGETS = {
  agents: '.agents/skills',
  claude: '.claude/skills',
  github: '.github/skills',
};

function printHelp() {
  console.log(`Usage: node scripts/sync-skills.mjs [options]

Reconcile .agents/skills, .claude/skills and .github/skills.

Options:
  --strategy <latest|base>           Sync strategy (default: latest)
  --base <agents|claude|github>      Source of truth / tiebreaker (default: agents)
  --dry-run                          Report planned changes without writing
  --no-delete                        Overlay copy instead of mirror (base strategy)
  --no-backup                        Accepted for CLI compatibility (no-op here)
  --backup-dir <path>                Accepted for CLI compatibility (no-op here)
  --verbose                          List every planned change
  -h, --help                         Show this help
`);
}

function requireValue(value, flag) {
  if (value === undefined) throw new Error(`${flag} requires a value`);
  return value;
}

function parseArgs(argv) {
  const opts = {
    strategy: 'latest',
    base: 'agents',
    dryRun: false,
    delete: true,
    verbose: false,
    help: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--strategy') {
      opts.strategy = requireValue(argv[i + 1], '--strategy').toLowerCase();
      i += 1;
    } else if (arg === '--base') {
      opts.base = normalizeBase(requireValue(argv[i + 1], '--base'));
      i += 1;
    } else if (arg === '--dry-run') {
      opts.dryRun = true;
    } else if (arg === '--no-delete') {
      opts.delete = false;
    } else if (arg === '--no-backup') {
      // safe-sync-skills.mjs handles backups before invoking this runner.
    } else if (arg === '--backup-dir') {
      requireValue(argv[i + 1], '--backup-dir');
      i += 1;
    } else if (arg === '--verbose') {
      opts.verbose = true;
    } else if (arg === '-h' || arg === '--help') {
      opts.help = true;
    } else {
      throw new Error(`Unknown option: ${arg}`);
    }
  }

  if (opts.strategy === 'auto') opts.strategy = 'latest';
  if (opts.strategy !== 'latest' && opts.strategy !== 'base') {
    throw new Error(`Invalid strategy: ${opts.strategy}`);
  }
  return opts;
}

function normalizeBase(value) {
  const v = String(value || '').toLowerCase();
  if (v in TARGETS) return v;
  if (v === 'auto') return 'agents';
  if (v === '.agents/skills') return 'agents';
  if (v === '.claude/skills') return 'claude';
  if (v === '.github/skills') return 'github';
  throw new Error(`Invalid base: ${value}`);
}

function toPosix(p) {
  return p.split(path.sep).join('/');
}

async function pathExists(target) {
  try {
    await stat(target);
    return true;
  } catch {
    return false;
  }
}

async function listFiles(rootAbs) {
  const files = new Map(); // relPath -> { abs, relPath, mtimeMs, hash }
  if (!(await pathExists(rootAbs))) return files;

  const stack = [rootAbs];
  while (stack.length > 0) {
    const dir = stack.pop();
    const entries = await readdir(dir, { withFileTypes: true });
    for (const entry of entries) {
      const abs = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        stack.push(abs);
        continue;
      }
      if (!entry.isFile()) continue;
      const st = await stat(abs);
      const bytes = await readFile(abs);
      const hash = createHash('sha256').update(bytes).digest('hex');
      const relPath = toPosix(path.relative(rootAbs, abs));
      files.set(relPath, { abs, relPath, mtimeMs: st.mtimeMs, hash });
    }
  }
  return files;
}

function buildDesired(strategy, baseName, states) {
  const desired = new Map(); // relPath -> { sourceName, abs, hash, mtimeMs }

  if (strategy === 'base') {
    const base = states[baseName];
    for (const [relPath, file] of base.files) {
      desired.set(relPath, { sourceName: baseName, abs: file.abs, hash: file.hash, mtimeMs: file.mtimeMs });
    }
    return desired;
  }

  // latest: union of all files, newest mtime wins (tiebreak: base, then TARGETS order)
  const order = [baseName, ...Object.keys(TARGETS).filter((n) => n !== baseName)];
  const byRel = new Map();
  for (const name of Object.keys(TARGETS)) {
    for (const [relPath, file] of states[name].files) {
      if (!byRel.has(relPath)) byRel.set(relPath, []);
      byRel.get(relPath).push({ name, ...file });
    }
  }
  for (const [relPath, entries] of byRel) {
    entries.sort((a, b) => b.mtimeMs - a.mtimeMs || order.indexOf(a.name) - order.indexOf(b.name));
    const winner = entries[0];
    desired.set(relPath, {
      sourceName: winner.name,
      abs: winner.abs,
      hash: winner.hash,
      mtimeMs: winner.mtimeMs,
    });
  }
  return desired;
}

async function pruneEmptyDirs(rootAbs) {
  if (!(await pathExists(rootAbs))) return;
  async function walk(dir) {
    const entries = await readdir(dir, { withFileTypes: true });
    for (const entry of entries) {
      if (entry.isDirectory()) await walk(path.join(dir, entry.name));
    }
    const remaining = await readdir(dir);
    if (remaining.length === 0 && path.resolve(dir) !== path.resolve(rootAbs)) {
      await rm(dir, { recursive: true, force: true });
    }
  }
  await walk(rootAbs);
}

async function main() {
  const opts = parseArgs(process.argv.slice(2));
  if (opts.help) {
    printHelp();
    return;
  }

  const repoRoot = process.cwd();
  const states = {};
  for (const [name, rel] of Object.entries(TARGETS)) {
    const abs = path.resolve(repoRoot, rel);
    states[name] = { name, rel, abs, files: await listFiles(abs) };
  }

  if (opts.strategy === 'base') {
    const base = states[opts.base];
    if (base.files.size === 0) {
      throw new Error(
        `Base "${opts.base}" (${TARGETS[opts.base]}) is empty or missing; refusing to mirror an empty base.`
      );
    }
  }

  const desired = buildDesired(opts.strategy, opts.base, states);

  const plannedWrites = [];
  const plannedDeletes = [];

  for (const name of Object.keys(TARGETS)) {
    const target = states[name];

    // Writes / updates
    for (const [relPath, want] of desired) {
      const destAbs = path.join(target.abs, relPath);
      if (path.resolve(want.abs) === path.resolve(destAbs)) continue; // source is this exact file
      const current = target.files.get(relPath);
      if (current && current.hash === want.hash) continue; // already up to date
      plannedWrites.push({ name, relPath, from: want.sourceName });
      if (!opts.dryRun) {
        await mkdir(path.dirname(destAbs), { recursive: true });
        await copyFile(want.abs, destAbs);
        // preserve the winning file's mtime so future runs stay stable
        const when = new Date(want.mtimeMs);
        try {
          await utimes(destAbs, when, when);
        } catch {
          /* mtime is best-effort */
        }
      }
    }

    // Deletions (mirror mode only). In "latest" the desired set is the union of
    // every file, so nothing is ever scheduled for deletion there.
    if (opts.delete) {
      for (const relPath of target.files.keys()) {
        if (!desired.has(relPath)) {
          plannedDeletes.push({ name, relPath });
          if (!opts.dryRun) {
            await rm(path.join(target.abs, relPath), { force: true });
          }
        }
      }
    }
  }

  if (!opts.dryRun && opts.delete && plannedDeletes.length > 0) {
    for (const name of Object.keys(TARGETS)) {
      await pruneEmptyDirs(states[name].abs);
    }
  }

  // Summary
  const mode = opts.dryRun ? 'dry-run' : 'apply';
  console.log(`sync-skills (${mode}): strategy=${opts.strategy}, base=${opts.base}`);
  for (const name of Object.keys(TARGETS)) {
    console.log(`- ${TARGETS[name]}: ${states[name].files.size} files`);
  }
  console.log(`Writes: ${plannedWrites.length}, Deletes: ${plannedDeletes.length}`);

  if (opts.verbose) {
    const previewW = plannedWrites.slice(0, 50);
    for (const w of previewW) {
      console.log(`  + ${TARGETS[w.name]}/${w.relPath} (from ${w.from})`);
    }
    if (plannedWrites.length > previewW.length) {
      console.log(`  ... ${plannedWrites.length - previewW.length} more writes`);
    }
    const previewD = plannedDeletes.slice(0, 50);
    for (const d of previewD) {
      console.log(`  - ${TARGETS[d.name]}/${d.relPath}`);
    }
    if (plannedDeletes.length > previewD.length) {
      console.log(`  ... ${plannedDeletes.length - previewD.length} more deletes`);
    }
  }

  if (opts.dryRun && plannedWrites.length + plannedDeletes.length > 0) {
    console.log('Dry run only. Re-run without --dry-run (or via safe-sync --apply) to apply.');
  }
}

main().catch((err) => {
  console.error(`sync-skills failed: ${err instanceof Error ? err.message : String(err)}`);
  process.exitCode = 1;
});
