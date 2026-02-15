## TM2020 Replay validator

### Description

CLI tool that validates TM2020 replays by simulating inputs with the real game physics (via Mania Server Manager in Docker).

### Requirements

- Docker
- wget available on PATH
- Nadeo Live credentials via environment variables:
    - NADEO_LOGIN
    - NADEO_PASSWORD


### Prerequisites

**WARNING:** This tool is still in super early development, please read through the source code to see what its doing before using it.
For this tool to work, you have to create a docker volume. By default, it’s called `msm_data`, but you can use a different name and specify it with the `--docker-volume` option.

```bash
$ docker volume create msm_data
$ tm20-replay-validator validate-map SWeykB70uNoBuM3ExlmFrEGuYRf --max-records 100
```

### Usage

Subcommands:
- validate-dir — validate all replays in a directory
- validate-map — fetch top records for a map from Nadeo and validate their ghosts

Global options (available to all subcommands):
- --docker-volume `<name>`
    - Docker volume used by Mania Server Manager
    - Default: msm_data
- --additional-maps `<path>`
    - Path to a folder with extra .Map.Gbx files to potentially reduce the number of maps to download
    - Note: currently used by validate-dir

---

#### validate-dir
- Description:
    - Scans a folder for .Replay.Gbx, .Ghost.Gbx, and .Map.Gbx files
    - Collects required maps, optionally downloads missing ones from Nadeo Live
    - Runs validations in a Dockerized Mania Server Manager
    - Prints JSON report to stdout mapping each input replay path to a validation result
- Arguments:
    - Replays folder
        - Path to a directory containing replays (or a single replay file)
- Options:
    - --download-missing-maps
        - If set, attempts to download any missing maps from Nadeo Live
        - Requires NADEO_LOGIN and NADEO_PASSWORD
        - Maps are saved to `<Replays folder>/maps`
- Output:
    - JSON object: `{ "<input replay path>": <MSMValidationResult or null>, ... }`

---

#### validate-map
- Description:
    - Looks up a map by its UID on Nadeo Live
    - Fetches the top N leaderboard entries for that map
    - Downloads each player’s ghost and validates it
    - Prints JSON report to stdout mapping leaderboard position to a validation result
- Arguments:
    - Map Id
        - The map UID (not the numeric MapId)
- Options:
    - --max-records `<int>`
        - How many top records to validate
        - Default: 10
- Output:
    - JSON object: `{ <position>: <MSMValidationResult or null>, ... }`
