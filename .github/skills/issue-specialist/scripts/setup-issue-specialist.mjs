#!/usr/bin/env node
import { cp, mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ISSUE_TEMPLATE_FILES = [
  'config.yml',
  '00-ai-assist.yml',
  '01-bug-report.yml',
  '02-feature-request.yml',
  '03-documentation.yml',
  '04-maintenance.yml',
];

function printHelp() {
  console.log(`Usage: node .github/skills/issue-specialist/scripts/setup-issue-specialist.mjs [options]

Issue運用基盤（日本語Issueフォーム + Issue作成CLI）をセットアップします。

Options:
  --target <path>   適用先リポジトリパス（既定: .）
  --with-cli        scripts/create-github-issue.mjs も配置する
  --force           既存ファイルを上書きする
  --dry-run         変更せずに実行内容のみ表示
  -h, --help        ヘルプを表示

Examples:
  node .github/skills/issue-specialist/scripts/setup-issue-specialist.mjs --target . --with-cli
  node .github/skills/issue-specialist/scripts/setup-issue-specialist.mjs --target "C:\\repos\\my-app" --with-cli --dry-run
`);
}

function parseArgs(argv) {
  const opts = {
    target: '.',
    withCli: false,
    force: false,
    dryRun: false,
    help: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--target') {
      const value = argv[i + 1];
      if (!value) throw new Error('--target には値が必要です');
      opts.target = value;
      i += 1;
      continue;
    }
    if (arg === '--with-cli') {
      opts.withCli = true;
      continue;
    }
    if (arg === '--force') {
      opts.force = true;
      continue;
    }
    if (arg === '--dry-run') {
      opts.dryRun = true;
      continue;
    }
    if (arg === '-h' || arg === '--help') {
      opts.help = true;
      continue;
    }
    throw new Error(`不明なオプション: ${arg}`);
  }

  return opts;
}

async function exists(filePath) {
  try {
    await stat(filePath);
    return true;
  } catch {
    return false;
  }
}

async function copyWithPolicy(src, dest, opts, counters) {
  const destExists = await exists(dest);
  const relative = path.relative(process.cwd(), dest).replace(/\\/g, '/');
  if (destExists && !opts.force) {
    console.log(`- SKIP: ${relative} (既存。上書きする場合は --force)`);
    counters.skipped += 1;
    return;
  }

  if (opts.dryRun) {
    console.log(`- DRY : ${relative}${destExists ? ' (上書き)' : ' (新規)'}`);
    if (destExists) counters.updated += 1;
    else counters.created += 1;
    return;
  }

  await mkdir(path.dirname(dest), { recursive: true });
  await cp(src, dest, { force: true, errorOnExist: false });
  if (destExists) {
    console.log(`- UPDT: ${relative}`);
    counters.updated += 1;
  } else {
    console.log(`- NEW : ${relative}`);
    counters.created += 1;
  }
}

async function updatePackageJson(targetRoot, opts, counters) {
  const packageJsonPath = path.join(targetRoot, 'package.json');
  if (!(await exists(packageJsonPath))) {
    console.log('- SKIP: package.json が存在しないため npm script 追加を省略');
    return;
  }

  const raw = await readFile(packageJsonPath, 'utf8');
  const pkg = JSON.parse(raw);
  if (!pkg.scripts || typeof pkg.scripts !== 'object') {
    pkg.scripts = {};
  }

  const desired = 'node scripts/create-github-issue.mjs';
  const current = pkg.scripts['issue:create'];
  const rel = path.relative(process.cwd(), packageJsonPath).replace(/\\/g, '/');

  if (current === desired) {
    console.log('- SKIP: package.json の scripts.issue:create は既に最新');
    return;
  }

  if (current && !opts.force) {
    console.log('- SKIP: package.json の scripts.issue:create は既存値あり（上書きは --force）');
    counters.skipped += 1;
    return;
  }

  if (opts.dryRun) {
    console.log(`- DRY : ${rel} (scripts.issue:create を${current ? '更新' : '追加'})`);
    if (current) counters.updated += 1;
    else counters.created += 1;
    return;
  }

  pkg.scripts['issue:create'] = desired;
  await writeFile(packageJsonPath, `${JSON.stringify(pkg, null, 2)}\n`, 'utf8');
  console.log(`- UPDT: ${rel} (scripts.issue:create)`);
  counters.updated += 1;
}

async function main() {
  let opts;
  try {
    opts = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`Error: ${error instanceof Error ? error.message : String(error)}`);
    printHelp();
    process.exit(1);
  }

  if (opts.help) {
    printHelp();
    return;
  }

  const __filename = fileURLToPath(import.meta.url);
  const __dirname = path.dirname(__filename);
  const skillRoot = path.resolve(__dirname, '..');
  const templatesRoot = path.join(skillRoot, 'assets', 'issue-templates');
  const cliScriptSrc = path.join(skillRoot, 'assets', 'scripts', 'create-github-issue.mjs');
  const targetRoot = path.resolve(process.cwd(), opts.target);
  const issueTemplateDest = path.join(targetRoot, '.github', 'ISSUE_TEMPLATE');

  const counters = { created: 0, updated: 0, skipped: 0 };

  console.log(`Target: ${targetRoot}`);
  console.log(`Mode  : ${opts.dryRun ? 'dry-run' : 'apply'}`);
  console.log(`Force : ${opts.force ? 'yes' : 'no'}`);
  console.log(`CLI   : ${opts.withCli ? 'with-cli' : 'templates-only'}`);

  for (const fileName of ISSUE_TEMPLATE_FILES) {
    const src = path.join(templatesRoot, fileName);
    const dest = path.join(issueTemplateDest, fileName);
    await copyWithPolicy(src, dest, opts, counters);
  }

  if (opts.withCli) {
    const cliDest = path.join(targetRoot, 'scripts', 'create-github-issue.mjs');
    await copyWithPolicy(cliScriptSrc, cliDest, opts, counters);
    await updatePackageJson(targetRoot, opts, counters);
  }

  console.log('\nSummary');
  console.log(`- Created: ${counters.created}`);
  console.log(`- Updated: ${counters.updated}`);
  console.log(`- Skipped: ${counters.skipped}`);
}

main().catch((error) => {
  console.error(`Unexpected error: ${error instanceof Error ? error.message : String(error)}`);
  process.exit(1);
});
