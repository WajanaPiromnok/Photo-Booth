CREATE TABLE IF NOT EXISTS booth_jobs (
    job_id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL,
    theme_id TEXT,
    status TEXT NOT NULL DEFAULT 'CREATED',
    payment_status TEXT NOT NULL DEFAULT 'UNKNOWN',
    upload_status TEXT NOT NULL DEFAULT 'PENDING',
    amount_minor_units BIGINT NOT NULL DEFAULT 0,
    currency_code TEXT NOT NULL DEFAULT 'THB',
    payment_reference TEXT,
    remote_asset_key TEXT,
    download_url TEXT,
    published_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_booth_jobs_status ON booth_jobs(status);
CREATE INDEX IF NOT EXISTS idx_booth_jobs_upload_status ON booth_jobs(upload_status);
CREATE INDEX IF NOT EXISTS idx_booth_jobs_device_id ON booth_jobs(device_id);

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
