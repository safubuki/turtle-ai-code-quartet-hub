#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { createInterface } from 'node:readline/promises';
import { stdin, stdout } from 'node:process';

const TYPE_CONFIG = {
  bug: {
    prefix: '[バグ]',
    defaultLabels: ['bug'],
  },
  enhancement: {
    prefix: '[改善]',
    defaultLabels: ['enhancement'],
  },
  docs: {
    prefix: '[ドキュメント]',
    defaultLabels: ['documentation'],
  },
  maintenance: {
    prefix: '[メンテ]',
    defaultLabels: [],
  },
};

function printHelp() {
  console.log(`Usage: node scripts/create-github-issue.mjs [options]

概要入力から構造化Issue本文を生成し、GitHub Issueを作成します。

Options:
  --type <bug|enhancement|docs|maintenance|バグ|改善|ドキュメント|メンテ>
  --summary <text>            概要（タイトルと本文に使用）
  --title <text>              タイトルを明示指定
  --context <text>            背景、影響、目的
  --steps <text>              手順またはタスク（';' または改行で分割）
  --expected <text>           期待される挙動（主にバグ）
  --actual <text>             実際の挙動（主にバグ）
  --proposal <text>           提案内容
  --acceptance <text>         受け入れ条件（';' または改行で分割）
  --scope <text>              スコープ、対象ファイル、対象外
  --labels <a,b,c>            追加ラベル
  --repo <owner/name>         対象リポジトリ（任意）
  --dry-run                   Issueは作成せず内容だけ表示
  --no-prompt                 必須項目が不足時に対話せず失敗
  -h, --help                  ヘルプ表示

Examples:
  node scripts/create-github-issue.mjs --type バグ --summary "iOSで書き出し失敗"
  node scripts/create-github-issue.mjs --type docs --summary "セットアップ手順を更新" --dry-run
  node scripts/create-github-issue.mjs
`);
}

function normalizeType(input) {
  const value = String(input || '').trim().toLowerCase();
  if (!value) return '';

  if (value === '1') return 'bug';
  if (value === '2') return 'enhancement';
  if (value === '3') return 'docs';
  if (value === '4') return 'maintenance';

  if (value === 'bug' || value === 'b' || value === 'バグ') return 'bug';
  if (value === 'enhancement' || value === 'feature' || value === 'feat' || value === 'e' || value === '改善' || value === '機能') {
    return 'enhancement';
  }
  if (value === 'docs' || value === 'doc' || value === 'documentation' || value === 'ドキュメント' || value === '文書') {
    return 'docs';
  }
  if (value === 'maintenance' || value === 'maint' || value === 'chore' || value === 'task' || value === 'メンテ') {
    return 'maintenance';
  }

  return '';
}

function parseList(value) {
  return String(value || '')
    .split(',')
    .map((v) => v.trim())
    .filter(Boolean);
}

function parseArgs(argv) {
  const opts = {
    type: '',
    summary: '',
    title: '',
    context: '',
    steps: '',
    expected: '',
    actual: '',
    proposal: '',
    acceptance: '',
    scope: '',
    labels: [],
    repo: '',
    dryRun: false,
    noPrompt: false,
    help: false,
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--type') {
      opts.type = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--summary') {
      opts.summary = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--title') {
      opts.title = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--context') {
      opts.context = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--steps') {
      opts.steps = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--expected') {
      opts.expected = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--actual') {
      opts.actual = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--proposal') {
      opts.proposal = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--acceptance') {
      opts.acceptance = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--scope') {
      opts.scope = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--labels') {
      opts.labels.push(...parseList(argv[i + 1]));
      i += 1;
      continue;
    }
    if (arg === '--repo') {
      opts.repo = argv[i + 1] || '';
      i += 1;
      continue;
    }
    if (arg === '--dry-run') {
      opts.dryRun = true;
      continue;
    }
    if (arg === '--no-prompt') {
      opts.noPrompt = true;
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

function splitLines(value) {
  return String(value || '')
    .split(/\r?\n|;/)
    .map((line) => line.trim())
    .filter(Boolean);
}

function numberedList(value, fallbackLines) {
  const lines = splitLines(value);
  const src = lines.length > 0 ? lines : fallbackLines;
  return src.map((line, index) => `${index + 1}. ${line}`).join('\n');
}

function checkboxList(value, fallbackLines) {
  const lines = splitLines(value);
  const src = lines.length > 0 ? lines : fallbackLines;
  return src.map((line) => `- [ ] ${line}`).join('\n');
}

function bulletList(value, fallbackLines) {
  const lines = splitLines(value);
  const src = lines.length > 0 ? lines : fallbackLines;
  return src.map((line) => `- ${line}`).join('\n');
}

function pickText(value, fallback) {
  const text = String(value || '').trim();
  return text || fallback;
}

function buildBody(opts) {
  if (opts.type === 'bug') {
    return `## 概要
${opts.summary}

## 再現手順
${numberedList(opts.steps, ['アプリを開く', '対象操作を実行する', '不具合を確認する'])}

## 期待される挙動
${pickText(opts.expected, '本来の挙動を記載してください。')}

## 実際の挙動
${pickText(opts.actual, '実際に起きた挙動やエラーを記載してください。')}

## 背景と影響
${pickText(opts.context, '影響範囲、発生頻度、重大度を記載してください。')}

## 完了条件
- [ ] 原因が特定されている
- [ ] 修正が実装されている
- [ ] 回帰確認（テスト追加または妥当な説明）がある
- [ ] 最新ビルドで確認済み
`;
  }

  if (opts.type === 'enhancement') {
    return `## 目的
${opts.summary}

## 背景・課題
${pickText(opts.context, '現状の課題とユーザー影響を記載してください。')}

## 提案内容
${pickText(opts.proposal, '提案する挙動と実装方針を記載してください。')}

## 受け入れ条件
${checkboxList(opts.acceptance, ['要件が実装されている', '挙動がテストまたは検証で確認されている', '関連ドキュメントが更新されている'])}

## スコープ外
${bulletList(opts.scope, ['特記事項なし'])}
`;
  }

  if (opts.type === 'docs') {
    return `## 概要
${opts.summary}

## 現状の課題
${pickText(opts.context, '不足・古い・分かりにくい点を記載してください。')}

## 改善内容
${pickText(opts.proposal, '実施するドキュメント変更を記載してください。')}

## 対象ファイル / 領域
${bulletList(opts.scope, ['README.md', 'Docs/developer_guide.md'])}

## 受け入れ条件
${checkboxList(opts.acceptance, ['技術的に正確である', '手順が再現可能である', '用語が一貫している'])}
`;
  }

  return `## 目的
${opts.summary}

## スコープ
${pickText(opts.scope, '対象範囲と対象外を記載してください。')}

## タスク分解
${checkboxList(opts.steps, ['メンテナンスタスクを実施する', '検証を実施する', '重要変更を記録する'])}

## 検証計画
${numberedList(opts.acceptance, ['関連テストとLintを実行する', '手動検証を実施する', '主要導線で回帰がないことを確認する'])}

## リスクと対策
${pickText(opts.context, '主要リスクと軽減策を記載してください。')}
`;
}

function buildIssue(opts) {
  const config = TYPE_CONFIG[opts.type];
  if (!config) {
    throw new Error(`Invalid issue type: ${opts.type}`);
  }

  const summary = opts.summary.trim();
  const title = opts.title.trim() || `${config.prefix} ${summary}`;
  const labels = [...new Set([...config.defaultLabels, ...opts.labels])];
  const body = buildBody(opts);
  return { title, body, labels };
}

function isGhAvailable() {
  const result = spawnSync('gh', ['--version'], {
    stdio: 'ignore',
    shell: process.platform === 'win32',
  });
  return result.status === 0;
}

async function promptForMissingFields(opts) {
  if (opts.noPrompt) return opts;

  const rl = createInterface({ input: stdin, output: stdout });
  try {
    if (!opts.type) {
      const answer = await rl.question(
        'Issue種別 [1:バグ, 2:改善, 3:ドキュメント, 4:メンテ]: '
      );
      opts.type = normalizeType(answer);
    }

    while (!opts.summary.trim()) {
      const answer = await rl.question('Issue概要: ');
      opts.summary = answer.trim();
    }

    if (opts.type === 'bug') {
      if (!opts.steps) opts.steps = await rl.question('再現手順（任意、";"区切り可）: ');
      if (!opts.expected) opts.expected = await rl.question('期待される挙動（任意）: ');
      if (!opts.actual) opts.actual = await rl.question('実際の挙動（任意）: ');
      if (!opts.context) opts.context = await rl.question('背景・影響（任意）: ');
    } else if (opts.type === 'enhancement') {
      if (!opts.context) opts.context = await rl.question('背景・課題（任意）: ');
      if (!opts.proposal) opts.proposal = await rl.question('提案内容（任意）: ');
      if (!opts.acceptance) opts.acceptance = await rl.question('受け入れ条件（任意、";"区切り可）: ');
      if (!opts.scope) opts.scope = await rl.question('スコープ外（任意、";"区切り可）: ');
    } else if (opts.type === 'docs') {
      if (!opts.context) opts.context = await rl.question('現状の課題（任意）: ');
      if (!opts.proposal) opts.proposal = await rl.question('改善内容（任意）: ');
      if (!opts.scope) opts.scope = await rl.question('対象ファイル（任意、";"区切り可）: ');
      if (!opts.acceptance) opts.acceptance = await rl.question('受け入れ条件（任意、";"区切り可）: ');
    } else if (opts.type === 'maintenance') {
      if (!opts.scope) opts.scope = await rl.question('スコープ（任意）: ');
      if (!opts.steps) opts.steps = await rl.question('タスク分解（任意、";"区切り可）: ');
      if (!opts.acceptance) opts.acceptance = await rl.question('検証計画（任意、";"区切り可）: ');
      if (!opts.context) opts.context = await rl.question('リスクと対策（任意）: ');
    }
  } finally {
    rl.close();
  }

  return opts;
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

  opts.type = normalizeType(opts.type);
  opts = await promptForMissingFields(opts);

  if (!opts.type) {
    console.error('Error: issue種別が必要です。--type または対話入力を使用してください。');
    process.exit(1);
  }
  if (!opts.summary.trim()) {
    console.error('Error: 概要が必要です。--summary または対話入力を使用してください。');
    process.exit(1);
  }

  const issue = buildIssue(opts);

  if (opts.dryRun) {
    console.log('[dry-run] Issueは作成されません。');
    console.log(`タイトル: ${issue.title}`);
    if (issue.labels.length > 0) {
      console.log(`ラベル: ${issue.labels.join(', ')}`);
    } else {
      console.log('ラベル: なし');
    }
    console.log('\n本文:\n');
    console.log(issue.body);
    return;
  }

  if (!isGhAvailable()) {
    console.error('Error: GitHub CLI (gh) がPATH上に見つかりません。');
    console.error('インストール: https://cli.github.com/');
    console.error('ヒント: --dry-run で生成内容だけ確認できます。');
    process.exit(1);
  }

  const args = ['issue', 'create', '--title', issue.title, '--body', issue.body];
  if (opts.repo.trim()) {
    args.push('--repo', opts.repo.trim());
  }
  for (const label of issue.labels) {
    args.push('--label', label);
  }

  const result = spawnSync('gh', args, {
    stdio: 'inherit',
    shell: process.platform === 'win32',
  });

  if (result.status !== 0) {
    process.exit(result.status || 1);
  }
}

main().catch((error) => {
  console.error(`Unexpected error: ${error instanceof Error ? error.message : String(error)}`);
  process.exit(1);
});
