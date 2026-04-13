#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { lstat, readdir, readFile, readlink, stat } from 'node:fs/promises';
import path from 'node:path';

const TARGETS = {
  agents: '.agents/skills',
  agent: '.agent/skills',
  github: '.github/skills',
};

function printHelp() {
  console.log(`Usage: node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs [options]

Safely synchronize:
  - .agents/skills
  - .agent/skills
  - .github/skills

Options:
  --apply                            Apply changes (default: dry-run)
  --strategy <auto|base|latest>      Sync strategy (default: auto)
  --base <auto|agents|agent|github>  Base directory (default: auto)
  --no-strict                        Allow base strategy even if conflicts exist
  --no-backup                        Disable pre-sync backup
  --backup-dir <path>                Backup directory (default: .skills-backups)
  --no-delete                        Overlay copy instead of mirror copy
  --verbose                          Show detailed state
  --json                             Output audit summary as JSON
  -h, --help                         Show this help

Examples:
  node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --verbose
  node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --apply
  node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --apply --strategy base --base github
  node .github/skills/skills-sync-guard/scripts/safe-sync-skills.mjs --apply --strategy latest
`);
}

function parseArgs(argv) {
  const opts = {
    apply: false,
    strategy: 'auto',
    base: 'auto',
    strict: true,
    backup: true,
    backupDir: '.skills-backups',
    deleteFirst: true,
    verbose: false,
    json: false,
    help: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];

    if (arg === '--apply') {
      opts.apply = true;
      continue;
    }
    if (arg === '--strategy') {
      const value = argv[i + 1];
      if (!value) throw new Error('--strategy requires a value');
      opts.strategy = normalizeStrategy(value, true);
      i += 1;
      continue;
    }
    if (arg === '--base') {
      const value = argv[i + 1];
      if (!value) throw new Error('--base requires a value');
      opts.base = normalizeBase(value, true);
      i += 1;
      continue;
    }
    if (arg === '--no-strict') {
      opts.strict = false;
      continue;
    }
    if (arg === '--no-backup') {
      opts.backup = false;
      continue;
    }
    if (arg === '--backup-dir') {
      const value = argv[i + 1];
      if (!value) throw new Error('--backup-dir requires a value');
      opts.backupDir = value;
      i += 1;
      continue;
    }
    if (arg === '--no-delete') {
      opts.deleteFirst = false;
      continue;
    }
    if (arg === '--verbose') {
      opts.verbose = true;
      continue;
    }
    if (arg === '--json') {
      opts.json = true;
      continue;
    }
    if (arg === '-h' || arg === '--help') {
      opts.help = true;
      continue;
    }

    throw new Error(`Unknown option: ${arg}`);
  }

  return opts;
}

function normalizeStrategy(value, allowAuto) {
  const v = String(value || '').toLowerCase();
  if (v === 'base' || v === 'latest') return v;
  if (allowAuto && v === 'auto') return v;
  throw new Error(`Invalid strategy: ${value}`);
}

function normalizeBase(value, allowAuto) {
  const v = String(value || '').toLowerCase();
  if (v in TARGETS) return v;
  if (allowAuto && v === 'auto') return v;
  if (v === '.agents/skills') return 'agents';
  if (v === '.agent/skills') return 'agent';
  if (v === '.github/skills') return 'github';
  throw new Error(`Invalid base: ${value}`);
}

function normalizeRel(filePath) {
  return filePath.replace(/\\/g, '/');
}

function isTrackedEntry(entry) {
  return entry.isFile() || entry.isSymbolicLink();
}

async function hashEntry(fullPath) {
  const st = await lstat(fullPath);
  if (st.isSymbolicLink()) {
    const linkTarget = await readlink(fullPath);
    return `symlink:${linkTarget}`;
  }
  if (!st.isFile()) {
    return null;
  }
  const bytes = await readFile(fullPath);
  const fileHash = createHash('sha256').update(bytes).digest('hex');
  return `file:${fileHash}`;
}

function chooseByNewest(entries, preferredOrder = []) {
  const priority = new Map(preferredOrder.map((name, idx) => [name, idx]));
  return [...entries].sort((a, b) => {
    if (b.mtimeMs !== a.mtimeMs) return b.mtimeMs - a.mtimeMs;
    const ap = priority.has(a.source) ? priority.get(a.source) : Number.MAX_SAFE_INTEGER;
    const bp = priority.has(b.source) ? priority.get(b.source) : Number.MAX_SAFE_INTEGER;
    if (ap !== bp) return ap - bp;
    return a.source.localeCompare(b.source);
  })[0];
}

async function existsAsDirectory(dirPath) {
  try {
    const st = await stat(dirPath);
    return st.isDirectory();
  } catch {
    return false;
  }
}

async function listFilesRecursive(rootDir) {
  const out = [];
  const stack = [rootDir];

  while (stack.length > 0) {
    const current = stack.pop();
    const entries = await readdir(current, { withFileTypes: true });
    for (const entry of entries) {
      const fullPath = path.join(current, entry.name);
      if (entry.isDirectory()) {
        stack.push(fullPath);
        continue;
      }
      if (!isTrackedEntry(entry)) continue;
      const st = await lstat(fullPath);
      const hash = await hashEntry(fullPath);
      if (hash == null) continue;
      out.push({
        absPath: fullPath,
        relPath: normalizeRel(path.relative(rootDir, fullPath)),
        hash,
        mtimeMs: st.mtimeMs,
      });
    }
  }

  return out;
}

async function collectTargetState(repoRoot, name, relPath) {
  const absPath = path.resolve(repoRoot, relPath);
  const exists = await existsAsDirectory(absPath);
  if (!exists) {
    return {
      name,
      relPath,
      absPath,
      exists: false,
      nonEmpty: false,
      fileCount: 0,
      newestMtimeMs: 0,
      filesByRelPath: new Map(),
    };
  }

  const rootStat = await stat(absPath);
  const files = await listFilesRecursive(absPath);
  let newestMtimeMs = rootStat.mtimeMs;
  const filesByRelPath = new Map();

  for (const file of files) {
    filesByRelPath.set(file.relPath, file);
    if (file.mtimeMs > newestMtimeMs) newestMtimeMs = file.mtimeMs;
  }

  return {
    name,
    relPath,
    absPath,
    exists: true,
    nonEmpty: files.length > 0,
    fileCount: files.length,
    newestMtimeMs,
    filesByRelPath,
  };
}

function buildFreshnessScores(targets) {
  const byRelPath = new Map();
  const scores = new Map(
    targets.map((target) => [
      target.name,
      {
        latestWins: 0,
        staleFiles: 0,
        missingFiles: 0,
        comparedFiles: 0,
      },
    ])
  );

  for (const target of targets) {
    for (const file of target.filesByRelPath.values()) {
      if (!byRelPath.has(file.relPath)) {
        byRelPath.set(file.relPath, []);
      }
      byRelPath.get(file.relPath).push({ ...file, source: target.name });
    }
  }

  const preference = Object.keys(TARGETS);

  for (const [relPath, entries] of byRelPath.entries()) {
    const newestEntry = chooseByNewest(entries, preference);
    const winnerScore = scores.get(newestEntry.source);
    winnerScore.latestWins += 1;

    for (const target of targets) {
      const score = scores.get(target.name);
      score.comparedFiles += 1;
      const entry = target.filesByRelPath.get(relPath) || null;
      if (!entry) {
        score.missingFiles += 1;
        continue;
      }
      if (entry.hash !== newestEntry.hash) {
        score.staleFiles += 1;
      }
    }
  }

  return scores;
}

function pickBaseCandidate(targets) {
  const existing = targets.filter((t) => t.exists);
  if (existing.length === 0) {
    throw new Error('None of the skill directories exist.');
  }

  const nonEmpty = existing.filter((t) => t.nonEmpty);
  const pool = nonEmpty.length > 0 ? nonEmpty : existing;
  const freshnessScores = buildFreshnessScores(pool);

  const ranked = [...pool].sort((a, b) => {
    const as = freshnessScores.get(a.name);
    const bs = freshnessScores.get(b.name);

    if (bs.latestWins !== as.latestWins) return bs.latestWins - as.latestWins;
    if (as.staleFiles !== bs.staleFiles) return as.staleFiles - bs.staleFiles;
    if (as.missingFiles !== bs.missingFiles) return as.missingFiles - bs.missingFiles;
    if (b.fileCount !== a.fileCount) return b.fileCount - a.fileCount;
    if (b.newestMtimeMs !== a.newestMtimeMs) return b.newestMtimeMs - a.newestMtimeMs;
    return a.name.localeCompare(b.name);
  });

  return {
    candidate: ranked[0],
    freshnessScores,
  };
}

function buildConflictReport(targets, baseName) {
  const byRelPath = new Map();
  for (const target of targets) {
    if (!target.exists) continue;
    for (const file of target.filesByRelPath.values()) {
      if (!byRelPath.has(file.relPath)) {
        byRelPath.set(file.relPath, []);
      }
      byRelPath.get(file.relPath).push({ ...file, source: target.name });
    }
  }

  const conflicts = [];
  const divergences = [];
  const preference = [baseName, ...Object.keys(TARGETS).filter((n) => n !== baseName)];

  for (const [relPath, entries] of byRelPath.entries()) {
    const baseEntry = entries.find((e) => e.source === baseName) || null;
    const newestEntry = chooseByNewest(entries, preference);
    const uniqueHashes = new Set(entries.map((e) => e.hash));

    if (!baseEntry) {
      conflicts.push({
        type: 'base-missing',
        relPath,
        base: baseName,
        newestSource: newestEntry.source,
        newestMtime: new Date(newestEntry.mtimeMs).toISOString(),
      });
      continue;
    }

    if (baseEntry.hash !== newestEntry.hash) {
      conflicts.push({
        type: 'base-outdated',
        relPath,
        base: baseName,
        newestSource: newestEntry.source,
        newestMtime: new Date(newestEntry.mtimeMs).toISOString(),
        baseMtime: new Date(baseEntry.mtimeMs).toISOString(),
      });
      continue;
    }

    if (uniqueHashes.size > 1) {
      divergences.push({
        relPath,
        canonicalSource: baseName,
      });
    }
  }

  conflicts.sort((a, b) => a.relPath.localeCompare(b.relPath));
  divergences.sort((a, b) => a.relPath.localeCompare(b.relPath));
  return { conflicts, divergences };
}

function determinePlan(opts, baseCandidate, conflicts) {
  const baseName = opts.base === 'auto' ? baseCandidate.name : opts.base;
  let strategy = opts.strategy;

  if (strategy === 'auto') {
    strategy = conflicts.length === 0 ? 'base' : 'latest';
  }

  if (strategy === 'base' && conflicts.length > 0 && opts.strict) {
    throw new Error(
      `Conflicts detected (${conflicts.length}). Base strategy is blocked in strict mode. Use --strategy latest or --no-strict.`
    );
  }

  return { strategy, baseName };
}

function buildSyncArgs(opts, plan) {
  const args = ['scripts/sync-skills.mjs', '--strategy', plan.strategy, '--base', plan.baseName];
  if (!opts.apply) args.push('--dry-run');
  if (!opts.backup) args.push('--no-backup');
  if (opts.backupDir) args.push('--backup-dir', opts.backupDir);
  if (!opts.deleteFirst) args.push('--no-delete');
  if (opts.verbose) args.push('--verbose');
  return args;
}

function printSummary(targets, baseCandidate, report, plan, opts, freshnessScores) {
  console.log('Skills sync audit summary:');
  for (const t of targets) {
    const newest = t.exists ? new Date(t.newestMtimeMs).toISOString() : 'n/a';
    console.log(`- ${t.relPath}: exists=${t.exists}, files=${t.fileCount}, newest=${newest}`);
  }
  console.log(`Base candidate: ${baseCandidate.relPath}`);
  console.log(`Resolved base: ${TARGETS[plan.baseName]}`);
  console.log(`Resolved strategy: ${plan.strategy}`);
  console.log(`Conflicts: ${report.conflicts.length}`);

  if (report.conflicts.length > 0) {
    const previewCount = Math.min(report.conflicts.length, 20);
    for (let i = 0; i < previewCount; i += 1) {
      const c = report.conflicts[i];
      console.log(`  - [${c.type}] ${c.relPath} (newest: ${TARGETS[c.newestSource]})`);
    }
    if (report.conflicts.length > previewCount) {
      console.log(`  ... ${report.conflicts.length - previewCount} more`);
    }
  }

  if (opts.verbose) {
    for (const t of targets) {
      const score = freshnessScores.get(t.name);
      if (!score) continue;
      console.log(
        `  score ${t.relPath}: latestWins=${score.latestWins}, staleFiles=${score.staleFiles}, missingFiles=${score.missingFiles}, comparedFiles=${score.comparedFiles}`
      );
    }
    console.log(`Divergences (base already newest): ${report.divergences.length}`);
  }
}

function toJson(targets, baseCandidate, report, plan, opts, freshnessScores) {
  return {
    mode: opts.apply ? 'apply' : 'dry-run',
    targets: targets.map((t) => ({
      name: t.name,
      relPath: t.relPath,
      exists: t.exists,
      fileCount: t.fileCount,
      newest: t.exists ? new Date(t.newestMtimeMs).toISOString() : null,
      freshness: freshnessScores.get(t.name) || null,
    })),
    baseCandidate: baseCandidate.name,
    baseCandidateFreshness: freshnessScores.get(baseCandidate.name) || null,
    resolvedBase: plan.baseName,
    resolvedStrategy: plan.strategy,
    conflicts: report.conflicts,
    divergences: report.divergences,
  };
}

async function runSync(repoRoot, args) {
  await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, args, {
      cwd: repoRoot,
      stdio: 'inherit',
    });
    child.on('error', reject);
    child.on('exit', (code) => {
      if (code === 0) resolve();
      else reject(new Error(`sync-skills.mjs exited with code ${code}`));
    });
  });
}

function ensureBaseExists(baseName, targets) {
  const target = targets.find((t) => t.name === baseName);
  if (!target || !target.exists) {
    throw new Error(`Selected base directory does not exist: ${TARGETS[baseName]}`);
  }
}

async function main() {
  const opts = parseArgs(process.argv.slice(2));
  if (opts.help) {
    printHelp();
    return;
  }

  const repoRoot = process.cwd();
  const targetEntries = await Promise.all(
    Object.entries(TARGETS).map(([name, relPath]) => collectTargetState(repoRoot, name, relPath))
  );

  const { candidate: baseCandidate, freshnessScores } = pickBaseCandidate(targetEntries);
  const auditBaseName = opts.base === 'auto' ? baseCandidate.name : opts.base;
  ensureBaseExists(auditBaseName, targetEntries);

  const report = buildConflictReport(targetEntries, auditBaseName);
  const plan = determinePlan(opts, baseCandidate, report.conflicts);
  const syncArgs = buildSyncArgs(opts, plan);

  if (opts.json) {
    console.log(JSON.stringify(toJson(targetEntries, baseCandidate, report, plan, opts, freshnessScores), null, 2));
  } else {
    printSummary(targetEntries, baseCandidate, report, plan, opts, freshnessScores);
    console.log(`Planned command: node ${syncArgs.join(' ')}`);
  }

  if (!opts.apply) {
    return;
  }

  try {
    await runSync(repoRoot, syncArgs);
  } catch (err) {
    if (!opts.deleteFirst) {
      throw err;
    }
    const retryOpts = { ...opts, deleteFirst: false };
    const retryArgs = buildSyncArgs(retryOpts, plan);
    console.warn('Mirror sync failed. Retrying with overlay mode (--no-delete).');
    if (!opts.json) {
      console.log(`Retry command: node ${retryArgs.join(' ')}`);
    }
    await runSync(repoRoot, retryArgs);
  }
}

main().catch((err) => {
  console.error(`Safe sync failed: ${err instanceof Error ? err.message : String(err)}`);
  process.exitCode = 1;
});
