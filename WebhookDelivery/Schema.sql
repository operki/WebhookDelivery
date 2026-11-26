CREATE TABLE IF NOT EXISTS events (
    id uuid PRIMARY KEY,
    idempotency_key varchar(128) NOT NULL UNIQUE,
    type varchar(100) NOT NULL,
    payload jsonb NOT NULL,
    destination text NOT NULL,
    status text NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending', 'Delivering', 'Delivered', 'Failed')),
    attempt_count integer NOT NULL DEFAULT 0,
    cycle_attempt_count integer NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    lease_id uuid,
    lease_expires_at timestamptz
);
CREATE INDEX IF NOT EXISTS events_pending ON events (next_attempt_at) WHERE status = 'Pending';
CREATE INDEX IF NOT EXISTS events_leases ON events (lease_expires_at) WHERE status = 'Delivering';

CREATE TABLE IF NOT EXISTS delivery_attempts (
    id uuid PRIMARY KEY,
    event_id uuid NOT NULL REFERENCES events(id),
    number integer NOT NULL,
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    status_code integer,
    error text,
    UNIQUE(event_id, number)
);
