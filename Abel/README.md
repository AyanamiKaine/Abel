# Abel CLI Tool

Abel is an opinionated C++ build runner built around CMake + Ninja.

## Commands

- `abel build [paths...] [--release|--debug|--configuration <name>] [--verbose]`
- `abel run [paths...] [--release|--debug|--configuration <name>] [--verbose]`
- `abel check [--verbose]`
- `abel list [--verbose]`
- `abel search <query> [--verbose]`
- `abel info <package[/variant]> [--verbose]`
- `abel init <name> [--type exe|module]`
- `abel init --list-templates`
- `abel add <dep...> [--project <path>]`
- `abel help`
- `abel version`

Configuration precedence for `build`/`run`: CLI (`--release/--debug/-c`) > `project.json` (`build.default_configuration`) > `Release`.

## Path Resolution

- If no path is provided, Abel uses the current directory when `project.json` exists.
- If a directory has no `project.json`, Abel scans immediate child directories for projects.
- A direct `project.json` file path is also accepted.
