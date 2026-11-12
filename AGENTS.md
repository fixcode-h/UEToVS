# UEToVS · 开发约定

给在这个仓库里工作的 AI 编码助手（Codex、Claude Code 等）和开发者看。`CLAUDE.md` 只是引用本文件，规则只改这一份。项目介绍、结构和构建命令见 [README.md](README.md)。

## 分支流程

- **所有开发都在 `dev` 分支上进行**：开始任何代码、文档或配置改动之前，先切到 `dev`：
  ```bash
  git fetch origin
  git switch dev            # 本地没有时：git switch -c dev origin/dev
  git status --short --branch   # 确认显示在 dev 上再动手
  ```
- **不在 `main` 上直接开发**：不在 `main` 上改文件、提交或推送，包括小改动和文档修改。误在 `main` 上改了，先切回 `dev` 再继续，不要用 `git checkout .`、`git reset --hard` 清掉别人的改动。
- **推送前先合并远端**：可能有多个助手同时往 `dev` 推，推送前先 `git fetch origin && git merge origin/dev`。不用 rebase（含 `git pull --rebase`），除非用户明确要求。
- **完成后合并到主干 `main`**：开发完成、本地验证通过后推送 `dev`，再建 `dev` → `main` 的 PR（已有未合并的就复用），等 PR 检查通过后普通合并（merge commit）。PR 冲突、检查失败或分支保护拦截时停下来报告，不绕过检查、不直接往 `main` 推。

## 提交前

1. `git status --short` 看清楚要提交的文件：新文件都已 add，测试或构建产物没有混进来。
2. 跑单元测试：`dotnet test`。
3. 改了 `src/` 或 `ue/` 时，在 [CHANGELOG.md](CHANGELOG.md) 最上面加新版本节 `## vX.Y.Z`（格式见文件开头），PR 检查会验证；最上面的版本已发布过就要新开一节，不要往已发布的版本里加条目。

## CI 与发布

- 推送到 `dev` / `main` 和每个 PR 都会跑「Build」（`.github/workflows/build.yml`）：单元测试、打包 VSIX；PR 还会检查 CHANGELOG。
- 合并到 `main` 后，如果 CHANGELOG 最上面的版本还没有发布，CI 自动打包 `UEToVS-X.Y.Z.vsix` 并发布 Release，说明由这一节生成。
