CREATE TABLE IF NOT EXISTS projects (
    id TEXT PRIMARY KEY,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    result_route_prefix TEXT NOT NULL DEFAULT 'world-tour',
    status TEXT NOT NULL DEFAULT 'ACTIVE',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO projects (id, code, name, result_route_prefix, status)
VALUES
    ('prj_main', 'MAIN', 'Main Photo Booth', 'd', 'ACTIVE'),
    ('prj_world_tour', 'WORLD_TOUR', 'World Tour Photo Booth', 'world-tour', 'ACTIVE'),
    ('prj_kooky_world', 'KOOKY_WORLD', 'Kooky World Photo Booth', 'kooky-world', 'ACTIVE')
ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS booth_jobs (
    job_id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL DEFAULT 'prj_world_tour' REFERENCES projects(id),
    device_id TEXT NOT NULL,
    theme_id TEXT,
    image_preview_id TEXT,
    passenger_name TEXT,
    checkout_status TEXT NOT NULL DEFAULT 'CHECKOUT_CREATED',
    status TEXT NOT NULL DEFAULT 'CREATED',
    payment_status TEXT NOT NULL DEFAULT 'UNKNOWN',
    upload_status TEXT NOT NULL DEFAULT 'PENDING',
    amount_minor_units BIGINT NOT NULL DEFAULT 0,
    currency_code TEXT NOT NULL DEFAULT 'THB',
    payment_reference TEXT,
    remote_asset_key TEXT,
    session_folder TEXT,
    session_started_at_utc TIMESTAMPTZ,
    download_url TEXT,
    voucher_redemption_id BIGINT,
    published_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (project_id, job_id)
);

CREATE INDEX IF NOT EXISTS idx_booth_jobs_status ON booth_jobs(status);
CREATE INDEX IF NOT EXISTS idx_booth_jobs_project_id ON booth_jobs(project_id);
CREATE INDEX IF NOT EXISTS idx_booth_jobs_checkout_status ON booth_jobs(checkout_status);
CREATE INDEX IF NOT EXISTS idx_booth_jobs_upload_status ON booth_jobs(upload_status);
CREATE INDEX IF NOT EXISTS idx_booth_jobs_device_id ON booth_jobs(device_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_booth_jobs_session_folder ON booth_jobs(session_folder) WHERE session_folder IS NOT NULL;

CREATE TABLE IF NOT EXISTS booth_assets (
    id BIGSERIAL PRIMARY KEY,
    job_id TEXT NOT NULL REFERENCES booth_jobs(job_id) ON DELETE CASCADE,
    asset_type TEXT NOT NULL,
    remote_key TEXT NOT NULL,
    content_type TEXT NOT NULL,
    checksum TEXT,
    file_size_bytes BIGINT,
    original_file_name TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (job_id, asset_type, remote_key)
);

CREATE INDEX IF NOT EXISTS idx_booth_assets_job_id ON booth_assets(job_id);
CREATE INDEX IF NOT EXISTS idx_booth_assets_type ON booth_assets(asset_type);

CREATE TABLE IF NOT EXISTS project_devices (
    id BIGSERIAL PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    device_id TEXT NOT NULL UNIQUE,
    api_key_hash TEXT,
    active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_project_devices_project_id ON project_devices(project_id);

INSERT INTO project_devices (project_id, device_id, active)
VALUES ('prj_kooky_world', 'booth-kooky-01', TRUE)
ON CONFLICT (device_id)
DO UPDATE SET project_id = EXCLUDED.project_id, active = TRUE, updated_at = NOW();

CREATE TABLE IF NOT EXISTS user_devices (
    id BIGSERIAL PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    device_token_hash TEXT NOT NULL UNIQUE,
    device_name TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'ACTIVE',
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_user_devices_project_id ON user_devices(project_id);
CREATE INDEX IF NOT EXISTS idx_user_devices_status ON user_devices(status);

CREATE TABLE IF NOT EXISTS voucher_campaigns (
    id BIGSERIAL PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    title TEXT NOT NULL,
    purpose TEXT,
    default_benefit_type TEXT,
    status TEXT NOT NULL DEFAULT 'ACTIVE',
    created_by TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_voucher_campaigns_project_id ON voucher_campaigns(project_id);

CREATE TABLE IF NOT EXISTS vouchers (
    id BIGSERIAL PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    campaign_id BIGINT REFERENCES voucher_campaigns(id),
    code_hash TEXT NOT NULL,
    code_masked TEXT NOT NULL,
    benefit_type TEXT NOT NULL,
    benefit_value_minor BIGINT NOT NULL DEFAULT 0,
    benefit_percent INTEGER NOT NULL DEFAULT 0,
    max_uses INTEGER NOT NULL DEFAULT 1,
    used_count INTEGER NOT NULL DEFAULT 0,
    valid_from TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    valid_until TIMESTAMPTZ,
    purpose TEXT,
    status TEXT NOT NULL DEFAULT 'ACTIVE',
    created_by TEXT,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (project_id, code_hash),
    UNIQUE (project_id, id)
);

CREATE INDEX IF NOT EXISTS idx_vouchers_project_id ON vouchers(project_id);
CREATE INDEX IF NOT EXISTS idx_vouchers_status ON vouchers(status);

CREATE TABLE IF NOT EXISTS voucher_redemptions (
    id BIGSERIAL PRIMARY KEY,
    project_id TEXT NOT NULL,
    voucher_id BIGINT NOT NULL,
    job_id TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'RESERVED',
    idempotency_key TEXT NOT NULL UNIQUE,
    gross_amount_minor BIGINT NOT NULL DEFAULT 0,
    discount_amount_minor BIGINT NOT NULL DEFAULT 0,
    net_amount_minor BIGINT NOT NULL DEFAULT 0,
    currency_code TEXT NOT NULL DEFAULT 'THB',
    reserved_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    applied_at TIMESTAMPTZ,
    released_at TIMESTAMPTZ,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    FOREIGN KEY (project_id, voucher_id) REFERENCES vouchers(project_id, id),
    FOREIGN KEY (project_id, job_id) REFERENCES booth_jobs(project_id, job_id)
);

CREATE INDEX IF NOT EXISTS idx_voucher_redemptions_project_id ON voucher_redemptions(project_id);
CREATE INDEX IF NOT EXISTS idx_voucher_redemptions_job_id ON voucher_redemptions(job_id);
CREATE INDEX IF NOT EXISTS idx_voucher_redemptions_voucher_id ON voucher_redemptions(voucher_id);
CREATE INDEX IF NOT EXISTS idx_voucher_redemptions_status ON voucher_redemptions(status);

CREATE TABLE IF NOT EXISTS voucher_device_links (
    id BIGSERIAL PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    device_id BIGINT NOT NULL REFERENCES user_devices(id) ON DELETE CASCADE,
    voucher_id BIGINT NOT NULL,
    voucher_code TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'ACTIVE',
    claim_source TEXT,
    claimed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    FOREIGN KEY (project_id, voucher_id) REFERENCES vouchers(project_id, id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_voucher_device_links_project_voucher ON voucher_device_links(project_id, voucher_id);
CREATE UNIQUE INDEX IF NOT EXISTS idx_voucher_device_links_project_code ON voucher_device_links(project_id, voucher_code);
CREATE INDEX IF NOT EXISTS idx_voucher_device_links_device_id ON voucher_device_links(device_id);
CREATE INDEX IF NOT EXISTS idx_voucher_device_links_status ON voucher_device_links(status);

CREATE TABLE IF NOT EXISTS kiosk_sessions (
    id BIGSERIAL PRIMARY KEY,
    session_token TEXT NOT NULL UNIQUE,
    project_id TEXT NOT NULL REFERENCES projects(id),
    device_id TEXT NOT NULL,
    job_id TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'PENDING',
    mobile_device_id BIGINT REFERENCES user_devices(id) ON DELETE SET NULL,
    voucher_link_id BIGINT REFERENCES voucher_device_links(id) ON DELETE SET NULL,
    voucher_code TEXT,
    voucher_status_json JSONB,
    qr_payload TEXT,
    expires_at TIMESTAMPTZ NOT NULL,
    attached_at TIMESTAMPTZ,
    redeemed_at TIMESTAMPTZ,
    cancelled_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_kiosk_sessions_token ON kiosk_sessions(session_token);
CREATE INDEX IF NOT EXISTS idx_kiosk_sessions_project_device ON kiosk_sessions(project_id, device_id);
CREATE INDEX IF NOT EXISTS idx_kiosk_sessions_status ON kiosk_sessions(status);
CREATE INDEX IF NOT EXISTS idx_kiosk_sessions_expires_at ON kiosk_sessions(expires_at);

CREATE TABLE IF NOT EXISTS kiosk_session_events (
    id BIGSERIAL PRIMARY KEY,
    kiosk_session_id BIGINT NOT NULL REFERENCES kiosk_sessions(id) ON DELETE CASCADE,
    event_name TEXT NOT NULL,
    event_status TEXT,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_kiosk_session_events_session_id ON kiosk_session_events(kiosk_session_id);
CREATE INDEX IF NOT EXISTS idx_kiosk_session_events_event_name ON kiosk_session_events(event_name);
CREATE INDEX IF NOT EXISTS idx_kiosk_session_events_created_at ON kiosk_session_events(created_at DESC);

CREATE TABLE IF NOT EXISTS payments (
    id BIGSERIAL PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id),
    job_id TEXT NOT NULL,
    voucher_redemption_id BIGINT REFERENCES voucher_redemptions(id),
    gross_amount_minor BIGINT NOT NULL DEFAULT 0,
    discount_amount_minor BIGINT NOT NULL DEFAULT 0,
    net_amount_minor BIGINT NOT NULL DEFAULT 0,
    currency_code TEXT NOT NULL DEFAULT 'THB',
    method TEXT,
    provider TEXT,
    provider_reference TEXT,
    status TEXT NOT NULL DEFAULT 'PENDING',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    FOREIGN KEY (project_id, job_id) REFERENCES booth_jobs(project_id, job_id)
);

CREATE INDEX IF NOT EXISTS idx_payments_project_job ON payments(project_id, job_id);

CREATE TABLE IF NOT EXISTS booth_events (
    id BIGSERIAL PRIMARY KEY,
    event_name TEXT NOT NULL,
    project_id TEXT REFERENCES projects(id),
    job_id TEXT,
    device_id TEXT NOT NULL,
    theme_id TEXT,
    screen_id TEXT,
    duration_seconds INTEGER,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_booth_events_event_name ON booth_events(event_name);
CREATE INDEX IF NOT EXISTS idx_booth_events_project_id ON booth_events(project_id);
CREATE INDEX IF NOT EXISTS idx_booth_events_job_id ON booth_events(job_id);
CREATE INDEX IF NOT EXISTS idx_booth_events_device_id ON booth_events(device_id);
CREATE INDEX IF NOT EXISTS idx_booth_events_created_at ON booth_events(created_at DESC);
