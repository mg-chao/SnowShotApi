# Repository Guidelines

## Commit & Pull Request Guidelines
- Follow Conventional Commits, using an English, lowercase imperative description:
  `<type>(<scope>): <description>`.
- Types in use: `feat`, `fix`, `refactor`, `perf`, `test`, `style`, `build`.
- Scopes mirror the affected feature or area, such as `chat`, `translation`, `latex`,
  `recognition`, `billing`, or `deploy`. Omit the scope when no single area applies.
- Keep the subject concise and describe the concrete change. Examples:
  - `fix(chat): preserve token usage when retrying requests`
  - `feat(latex): add formula extraction support`
  - `refactor(billing): centralize token tariff calculation`
- PRs: state motivation, list affected projects or services, link relevant issues,
  and confirm format, lint, and related tests pass.
- Run only tests that cover the changes. Do not run the full suite for a commit or
  PR unless the user explicitly asks.
