# Abel CLI Tool

Abel is an opinionated C++ build runner built around CMake + Ninja.

## Commands

- `abel build [paths...] [--release|--debug|--configuration <name>] [--verbose]`
- `abel run [paths...] [--release|--debug|--configuration <name>] [--verbose]`
- `abel check [--verbose]`
- `abel list [--verbose]`
- `abel search <query> [--verbose]`
- `abel info <package[/variant]> [--verbose]`
- `abel init <name> [--type exe|module]` (also bootstraps git + `.gitignore`)
- `abel init --list-templates`
- `abel add <dep...> [--project <path>]`
- `abel module <name> [--project <path>] [--dir <relative-path>] [--partition <name> ...]`
- `abel help`
- `abel version`

Configuration precedence for `build`/`run`: CLI (`--release/--debug/-c`) > `project.json` (`build.default_configuration`) > `Release`.
Legacy header/src layout: set `build.legacy_header_src_layout` to `true` in `project.json`.
For `abel module`, `--project` is optional. Abel searches upward and uses the nearest parent `project.json` when omitted.
`abel init` creates a default C++ `.gitignore` and runs `git init` automatically. If git is unavailable, initialization still succeeds and prints a warning.

## Path Resolution

- If no path is provided, Abel uses the current directory when `project.json` exists.
- If a directory has no `project.json`, Abel scans immediate child directories for projects.
- A direct `project.json` file path is also accepted.
