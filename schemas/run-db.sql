PRAGMA foreign_keys = ON;
PRAGMA user_version = 1;

CREATE TABLE run (
    singleton               INTEGER PRIMARY KEY CHECK (singleton = 1),
    run_id                  TEXT NOT NULL UNIQUE,
    database_schema_version INTEGER NOT NULL,
    tool_version            TEXT NOT NULL,
    suite_id                TEXT,
    status                  TEXT NOT NULL CHECK (
        status IN ('CREATED', 'RUNNING', 'COMPLETED', 'COMPLETED_WITH_ERRORS', 'ABORTED')
    ),
    started_at_utc          TEXT NOT NULL,
    ended_at_utc            TEXT,
    timezone                TEXT NOT NULL,
    hostname                TEXT NOT NULL,
    machine_id              TEXT,
    os_version              TEXT NOT NULL,
    architecture            TEXT NOT NULL,
    boot_id                 TEXT,
    environment_json        TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(environment_json)),
    finalized               INTEGER NOT NULL DEFAULT 0 CHECK (finalized IN (0, 1))
);

CREATE TABLE capability_run (
    case_run_id             TEXT PRIMARY KEY,
    run_id                  TEXT NOT NULL REFERENCES run(run_id),
    sequence_number         INTEGER NOT NULL CHECK (sequence_number > 0),
    capability_id           TEXT NOT NULL,
    capability_version      TEXT NOT NULL,
    manifest_sha256         TEXT NOT NULL,
    baseline_id             TEXT NOT NULL,
    baseline_version        TEXT NOT NULL,
    nonce                   TEXT NOT NULL,
    risk_level              TEXT NOT NULL CHECK (risk_level IN ('L0', 'L1', 'L2', 'L3')),
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
    error_code              TEXT,
    error_message           TEXT,
    UNIQUE (run_id, sequence_number),
    UNIQUE (run_id, case_run_id)
);

CREATE TABLE program_instance (
    program_instance_id     TEXT PRIMARY KEY,
    case_run_id             TEXT NOT NULL REFERENCES capability_run(case_run_id),
    role                    TEXT NOT NULL CHECK (role IN ('controller', 'actor', 'target', 'helper')),
    instance_index          INTEGER NOT NULL DEFAULT 0 CHECK (instance_index >= 0),
    executable_path         TEXT NOT NULL,
    command_line            TEXT,
    sha256                  TEXT NOT NULL,
    md5                     TEXT,
    pid                     INTEGER NOT NULL CHECK (pid >= 0),
    started_at_utc          TEXT NOT NULL,
    ended_at_utc            TEXT,
    exit_code               INTEGER,
    metadata_json           TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(metadata_json)),
    UNIQUE (case_run_id, role, instance_index)
);

CREATE TABLE local_event (
    local_event_id          TEXT PRIMARY KEY,
    case_run_id             TEXT NOT NULL REFERENCES capability_run(case_run_id),
    event_type              TEXT NOT NULL,
    event_action            TEXT NOT NULL,
    observed_at_utc         TEXT NOT NULL,
    monotonic_offset_ms     INTEGER NOT NULL CHECK (monotonic_offset_ms >= 0),
    source                  TEXT NOT NULL,
    confidence              TEXT NOT NULL CHECK (confidence IN ('high', 'medium', 'low')),
    actor_program_id        TEXT REFERENCES program_instance(program_instance_id),
    target_program_id       TEXT REFERENCES program_instance(program_instance_id),
    data_json               TEXT NOT NULL CHECK (json_valid(data_json))
);

CREATE TABLE local_fact (
    local_fact_id           INTEGER PRIMARY KEY AUTOINCREMENT,
    case_run_id             TEXT NOT NULL REFERENCES capability_run(case_run_id),
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
    sha256                  TEXT NOT NULL,
    size_bytes              INTEGER NOT NULL CHECK (size_bytes >= 0),
    created_at_utc          TEXT NOT NULL,
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
    cleanup_result_id       INTEGER PRIMARY KEY AUTOINCREMENT,
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

CREATE INDEX ix_local_event_case_time
    ON local_event (case_run_id, observed_at_utc);

CREATE INDEX ix_local_event_type_action
    ON local_event (event_type, event_action);

CREATE INDEX ix_execution_log_case_time
    ON execution_log (case_run_id, timestamp_utc);
