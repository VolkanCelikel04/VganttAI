create extension if not exists pgcrypto;
create extension if not exists citext;

create table if not exists tenants (
    id uuid primary key default gen_random_uuid(),
    name text not null,
    code citext unique not null,
    is_active boolean not null default true,
    created_at timestamptz not null default now()
);

create table if not exists users (
    id uuid primary key default gen_random_uuid(),
    username citext unique not null,
    password_hash text not null,
    display_name text not null,
    is_active boolean not null default true,
    last_login_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists user_tenants (
    user_id uuid not null references users(id) on delete cascade,
    tenant_id uuid not null references tenants(id) on delete cascade,
    role text not null default 'user',
    created_at timestamptz not null default now(),
    primary key (user_id, tenant_id)
);

create table if not exists tenant_db_connections (
    id uuid primary key default gen_random_uuid(),
    tenant_id uuid not null references tenants(id) on delete cascade,
    host text not null,
    port integer not null default 5432,
    database_name text not null,
    username text not null,
    encrypted_password text not null,
    ssl_mode text not null default 'require',
    is_active boolean not null default true
);

create table if not exists audit_logs (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references users(id),
    tenant_id uuid not null references tenants(id),
    question text not null,
    generated_sql text,
    status text not null,
    error_message text,
    created_at timestamptz not null default now()
);

create index if not exists idx_user_tenants_tenant_id
    on user_tenants (tenant_id);

create unique index if not exists idx_tenant_db_connections_active_tenant
    on tenant_db_connections (tenant_id)
    where is_active = true;

create index if not exists idx_audit_logs_tenant_created_at
    on audit_logs (tenant_id, created_at desc);

create index if not exists idx_audit_logs_user_created_at
    on audit_logs (user_id, created_at desc);

alter table user_tenants
    drop constraint if exists chk_user_tenants_role;

alter table user_tenants
    add constraint chk_user_tenants_role
    check (role in ('admin', 'user', 'readonly'));

alter table tenant_db_connections
    drop constraint if exists chk_tenant_db_connections_port;

alter table tenant_db_connections
    add constraint chk_tenant_db_connections_port
    check (port between 1 and 65535);
