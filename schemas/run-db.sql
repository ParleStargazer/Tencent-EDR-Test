PRAGMA foreign_keys = ON;
PRAGMA user_version = 2;

CREATE TABLE run (
    singleton               INTEGER PRIMARY KEY CHECK (singleton = 1),
    run_id                  TEXT NOT NULL UNIQUE,
    database_schema_version INTEGER NOT NULL CHECK (database_schema_version = 2),
    tool_version            TEXT NOT NULL,
    suite_id                TEXT,
    environment_id          TEXT,
    status                  TEXT NOT NULL CHECK (
        status IN ('CREATED', 'RUNNING', 'COMPLETED', 'COMPLETED_WITH_ERRORS', 'ABORTED')
    ),
    started_at_utc          TEXT NOT NULL,
    ended_at_utc            TEXT,
    timezone                TEXT NOT NULL,
    utc_offset_minutes      INTEGER CHECK (utc_offset_minutes BETWEEN -840 AND 840),
    hostname                TEXT NOT NULL,
    machine_id              TEXT,
    os_family               TEXT NOT NULL DEFAULT 'windows' CHECK (os_family = 'windows'),
    os_version              TEXT NOT NULL,
    os_build                INTEGER NOT NULL CHECK (os_build > 0),
    os_edition              TEXT,
    architecture            TEXT NOT NULL CHECK (architecture IN ('x86', 'x64', 'arm64')),
    boot_id                 TEXT,
    boot_time_utc           TEXT NOT NULL,
    domain_name             TEXT,
    primary_user_sid        TEXT,
    agent_id_hint           TEXT,
    agent_version_hint      TEXT,
    clock_json              TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(clock_json)),
    environment_json        TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(environment_json)),
    finalized               INTEGER NOT NULL DEFAULT 0 CHECK (finalized IN (0, 1))
);

CREATE TABLE capability_run (
    case_run_id             TEXT PRIMARY KEY,
    run_id                  TEXT NOT NULL REFERENCES run(run_id),
    sequence_number         INTEGER NOT NULL CHECK (sequence_number > 0),
    capability_id           TEXT NOT NULL,
    display_name_zh         TEXT,
    display_name_en         TEXT,
    category                TEXT,
    capability_version      TEXT NOT NULL,
    manifest_sha256         TEXT NOT NULL,
    baseline_id             TEXT,
    baseline_version        TEXT,
    nonce                   TEXT NOT NULL,
    risk_level              TEXT NOT NULL CHECK (risk_level IN ('L0', 'L1', 'L2', 'L3')),
    required_privilege      TEXT CHECK (required_privilege IN ('standard_user', 'administrator', 'system')),
    status                  TEXT NOT NULL CHECK (
        status IN (
            'PLANNED', 'PRECHECK', 'EXECUTING', 'SELF_VERIFY', 'CLEANUP',
            'LOCAL_PASS', 'SAMPLE_ERROR', 'CLEANUP_ERROR', 'SKIPPED', 'ABORTED'
        )
    ),
    started_at_utc          TEXT,
    ended_at_utc            TEXT,
    monotonic_duration_ms   INTEGER CHECK (monotonic_duration_ms IS NULL OR monotonic_duration_ms >= 0),
    parameters_json         TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(parameters_json)),
    preconditions_json      TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(preconditions_json)),
    observer_started_at_utc TEXT,
    observer_ended_at_utc   TEXT,
    observer_sources_json   TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(observer_sources_json)),
    observer_dropped_count  INTEGER NOT NULL DEFAULT 0 CHECK (observer_dropped_count >= 0),
    observer_warnings_json  TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(observer_warnings_json)),
    error_code              TEXT,
    error_message           TEXT,
    UNIQUE (run_id, sequence_number),
    UNIQUE (run_id, case_run_id)
);

CREATE TABLE program_instance (
    program_instance_id     TEXT PRIMARY KEY,
    case_run_id             TEXT NOT NULL REFERENCES capability_run(case_run_id),
    role                    TEXT NOT NULL CHECK (role IN ('controller', 'actor', 'target', 'helper')),
    instance_name           TEXT,
    instance_index          INTEGER NOT NULL DEFAULT 0 CHECK (instance_index >= 0),
    executable_path         TEXT NOT NULL,
    file_name               TEXT,
    file_size_bytes         INTEGER CHECK (file_size_bytes IS NULL OR file_size_bytes >= 0),
    file_created_at_utc     TEXT,
    file_modified_at_utc    TEXT,
    sha256                  TEXT NOT NULL,
    sha1                    TEXT,
    md5                     TEXT,
    imphash                 TEXT,
    signature_json          TEXT CHECK (signature_json IS NULL OR json_valid(signature_json)),
    pid                     INTEGER NOT NULL CHECK (pid >= 0),
    parent_pid              INTEGER NOT NULL CHECK (parent_pid >= 0),
    session_id              INTEGER CHECK (session_id IS NULL OR session_id >= 0),
    architecture            TEXT NOT NULL CHECK (architecture IN ('x86', 'x64', 'arm64', 'unknown')),
    command_line            TEXT NOT NULL,
    working_directory       TEXT,
    user_sid                TEXT,
    user_name               TEXT,
    user_domain             TEXT,
    integrity_level         TEXT,
    elevation_type          TEXT,
    started_at_utc          TEXT NOT NULL,
    ended_at_utc            TEXT,
    exit_code               INTEGER,
    startup_attempted       INTEGER NOT NULL CHECK (startup_attempted IN (0, 1)),
    startup_succeeded       INTEGER NOT NULL CHECK (startup_succeeded IN (0, 1)),
    startup_win32_error     INTEGER CHECK (startup_win32_error IS NULL OR startup_win32_error >= 0),
    startup_message         TEXT,
    metadata_json           TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(metadata_json)),
    UNIQUE (case_run_id, role, instance_index)
);

CREATE TABLE local_event (
    local_event_id          TEXT PRIMARY KEY,
    case_run_id             TEXT NOT NULL REFERENCES capability_run(case_run_id),
    sequence_number         INTEGER NOT NULL CHECK (sequence_number > 0),
    event_type              TEXT NOT NULL CHECK (
        event_type IN (
            'process', 'file', 'account', 'network', 'hash', 'registry',
            'scheduled_task', 'service', 'driver', 'device', 'group_policy',
            'named_pipe', 'edr_sysops', 'wmi', 'bits', 'powershell'
        )
    ),
    event_action            TEXT NOT NULL,
    nonce                   TEXT NOT NULL,
    occurred_at_utc         TEXT NOT NULL,
    observed_at_utc         TEXT NOT NULL,
    monotonic_offset_ms     INTEGER NOT NULL CHECK (monotonic_offset_ms >= 0),
    source                  TEXT NOT NULL,
    collection_method       TEXT NOT NULL,
    collector_version       TEXT,
    confidence              TEXT NOT NULL CHECK (confidence IN ('high', 'medium', 'low')),
    actor_program_id        TEXT REFERENCES program_instance(program_instance_id),
    target_program_id       TEXT REFERENCES program_instance(program_instance_id),
    data_json               TEXT NOT NULL CHECK (json_valid(data_json)),
    evidence_refs_json      TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(evidence_refs_json)),
    CHECK (json_extract(data_json, '$.kind') = event_type),
    CHECK (json_extract(data_json, '$.operation') = event_action),
    UNIQUE (case_run_id, sequence_number)
);

CREATE TABLE local_fact (
    local_fact_id           TEXT PRIMARY KEY,
    case_run_id             TEXT NOT NULL REFERENCES capability_run(case_run_id),
    local_event_id          TEXT REFERENCES local_event(local_event_id),
    fact_key                TEXT NOT NULL,
    value_json              TEXT NOT NULL CHECK (json_valid(value_json)),
    value_type              TEXT NOT NULL CHECK (
        value_type IN ('string', 'integer', 'number', 'boolean', 'null', 'object', 'array')
    ),
    observed_at_utc         TEXT NOT NULL,
    source                  TEXT NOT NULL,
    confidence              TEXT NOT NULL CHECK (confidence IN ('high', 'medium', 'low')),
    UNIQUE (case_run_id, fact_key)
);

CREATE TABLE artifact (
    artifact_id             TEXT PRIMARY KEY,
    case_run_id             TEXT NOT NULL REFERENCES capability_run(case_run_id),
    kind                    TEXT NOT NULL,
    relative_path           TEXT NOT NULL,
    media_type              TEXT,
    sha256                  TEXT NOT NULL,
    size_bytes              INTEGER NOT NULL CHECK (size_bytes >= 0),
    created_at_utc          TEXT NOT NULL,
    sensitive               INTEGER NOT NULL DEFAULT 0 CHECK (sensitive IN (0, 1)),
    metadata_json           TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(metadata_json)),
    UNIQUE (case_run_id, relative_path)
);

CREATE TABLE execution_log (
    log_id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    case_run_id             TEXT REFERENCES capability_run(case_run_id),
    timestamp_utc           TEXT NOT NULL,
    level                   TEXT NOT NULL CHECK (level IN ('trace', 'debug', 'info', 'warning', 'error', 'critical')),
    phase                   TEXT NOT NULL,
    code                    TEXT,
    message                 TEXT NOT NULL,
    properties_json         TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(properties_json))
);

CREATE TABLE cleanup_result (
    cleanup_result_id       TEXT PRIMARY KEY,
    case_run_id             TEXT NOT NULL REFERENCES capability_run(case_run_id),
    sequence_number         INTEGER NOT NULL CHECK (sequence_number > 0),
    action                  TEXT NOT NULL,
    status                  TEXT NOT NULL CHECK (status IN ('succeeded', 'failed', 'skipped')),
    started_at_utc          TEXT NOT NULL,
    ended_at_utc            TEXT NOT NULL,
    before_json             TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(before_json)),
    after_json              TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(after_json)),
    error_message           TEXT,
    UNIQUE (case_run_id, sequence_number)
);

CREATE INDEX ix_capability_run_run_status
    ON capability_run (run_id, status);

CREATE INDEX ix_program_instance_case_role
    ON program_instance (case_run_id, role);

CREATE INDEX ix_program_instance_pid_start
    ON program_instance (pid, started_at_utc);

CREATE INDEX ix_local_event_case_time
    ON local_event (case_run_id, occurred_at_utc);

CREATE INDEX ix_local_event_type_action
    ON local_event (event_type, event_action);

CREATE INDEX ix_local_fact_case_key
    ON local_fact (case_run_id, fact_key);

CREATE INDEX ix_execution_log_case_time
    ON execution_log (case_run_id, timestamp_utc);
