# Abel

Abel is an opinionated C++ build runner on top of CMake + Ninja. Its cool when you want to create a new modern C++ project using modules for example. IT COULD NEVER BE USED TO REPLACE ALREADY USED SOLUTIONS.

Abel has only one real clear goal, be able to say `abel run` in a C++ project, and it just works. Sane defaults all around. If your ever thing "Mhh why doesn't this just work with Abel?" it's probably a bug that needs fixing asap.

It provides a simple CLI for:

- initializing new projects
- adding dependencies (with transitive registry resolution)
- building and running projects
- searching curated third-party packages

## Requirements

- .NET SDK 10
- CMake
- Ninja
- C++ toolchain (MSVC/Clang/GCC)

Run:

```powershell
abel check
```

to validate your local toolchain.

## Install (using dotnet)

```powershell
  dotnet tool install --global Abel.Tool
```

## Install (from this repo)

```powershell
dotnet pack Abel/Abel.csproj -c Release -o artifacts/nupkg
dotnet tool install --global Abel.Tool --add-source artifacts/nupkg --version 0.1.4
```

If already installed:

```powershell
dotnet tool update --global Abel.Tool --add-source artifacts/nupkg --version 0.1.4
```

## Quick Start

Create an executable project:

```powershell
abel init my_app
cd my_app
abel run
```

Create a module library:

```powershell
abel init my_module --type module
cd my_module
abel build
```

`abel init` creates a default C++ `.gitignore` and runs `git init` automatically. If git is unavailable, initialization still succeeds and prints a warning.

Create a local module inside an existing app and wire it as dependency:

```powershell
cd my_app
abel module gameplay
abel build
```

Create a nested module chain (`root -> gameplay -> ai`):

```powershell
cd my_app
abel module gameplay
abel module ai --project ./gameplay
abel build
```

Create a module with partitions:

```powershell
abel module gameplay --partition ecs --partition systems.pathing
```

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

For `abel module`, `--project` is optional. When omitted, Abel searches upward from the current directory and uses the nearest parent `project.json`.

## project.json

Executable:

```json
{
	"name": "game",
	"output_type": "exe",
	"cxx_standard": 23,
	"dependencies": ["sdl3", "imgui/sdl3_renderer", "flecs"]
}
```

With build settings:

```json
{
	"name": "game",
	"output_type": "exe",
	"cxx_standard": 23,
	"dependencies": [],
	"build": {
		"default_configuration": "RelWithDebInfo",
		"compile_options": {
			"common": ["-fno-omit-frame-pointer"],
			"msvc": ["/utf-8"],
			"gcc": [],
			"clang": []
		},
		"configurations": {
			"Release": {
				"compile_options": {
					"common": ["-march=native"],
					"msvc": ["/GL"],
					"gcc": ["-flto"],
					"clang": ["-flto"]
				}
			}
		}
	}
}
```

Notes:

- `build.default_configuration` is used when `abel build/run` is called without `--release`, `--debug`, or `--configuration`.
- CLI configuration flags always override `project.json`.
- Configuration keys under `build.configurations` support: `Debug`, `Release`, `RelWithDebInfo`, `MinSizeRel`.
- Set `build.legacy_header_src_layout` to `true` to support classic `include/` + `src/` projects without modules.

Module library:

```json
{
	"name": "math_module",
	"output_type": "library",
	"cxx_standard": 23,
	"sources": {
		"modules": ["src/math.cppm"],
		"private": ["src/math_impl.cpp"]
	},
	"dependencies": [],
	"tests": {
		"files": []
	}
}
```

## Dependency Registry

Registry packages can be discovered with:

```powershell
abel list
abel search sdl
abel info imgui
```

Dependency specs support optional variants:

- `imgui` (base package)
- `imgui/sdl3_renderer` (package variant)
- `math_module@https://github.com/<owner>/math_module.git` (git-hosted Abel module)
- `math_module@https://github.com/<owner>/math_module.git#v1.0.0` (git dependency pinned to tag/branch/commit)

`abel add` supports typo suggestions and will prompt with `Did you mean ...` when possible.

### Third Party Dependencies

**THIS IS NOT A PACKAGE MANAGER!** It is nothing more as a curated way for SOME library to be added to project as dependencies. See the list to see all currently supported ones.

## Why Name it Abel?

Because god is testing me with using C++ instead of abandoning it. I just want to be able to say Abel run the shit just works.

## Build UX

For non-verbose builds, Abel prints step-level progress, including:

- dependency fetch/build plan
- CMake file generation
- configure/build/install phases
- live activity details during long-running phases

## Repository Layout

- `Abel/` - CLI entrypoint and tool packaging
- `Abel.Core/` - build engine, CMake generation, registry, tool checks
- `Example/` - sample Abel projects

## Release Script

Use the release helper to bump the tool version, commit, and create the release tag in one command:

```powershell
./scripts/release.ps1 0.1.6
```

Or push commit + tag immediately:

```powershell
./scripts/release.ps1 0.1.6 -Push
```

Notes:

- The script requires a clean git working tree.
- The release workflow expects a matching git tag: `v<version>`.
