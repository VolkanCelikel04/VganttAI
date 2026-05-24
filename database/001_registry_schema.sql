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

create table if not exists mobile_login_sessions (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references users(id) on delete cascade,
    tenant_id uuid not null references tenants(id) on delete cascade,
    access_token_hash text unique not null,
    device_id text,
    device_name text,
    platform text,
    app_version text,
    push_token text,
    ip_address inet,
    expires_at timestamptz not null,
    revoked_at timestamptz,
    last_seen_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists tenant_db_connections (
    id uuid primary key default gen_random_uuid(),
    tenant_id uuid not null references tenants(id) on delete cascade,
    provider text not null default 'postgresql',
    host text not null,
    port integer not null default 5432,
    database_name text not null,
    username text not null,
    password_value text not null,
    ssl_mode text not null default 'require',
    is_active boolean not null default true
);

create table if not exists tenant_erp_objects (
    id uuid primary key default gen_random_uuid(),
    tenant_id uuid not null references tenants(id) on delete cascade,
    connection_id uuid references tenant_db_connections(id) on delete set null,
    object_name text not null,
    object_type text not null default 'view',
    business_domain text not null,
    display_name_tr text,
    description text,
    description_tr text,
    is_queryable boolean not null default true,
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists tenant_erp_object_columns (
    id uuid primary key default gen_random_uuid(),
    object_id uuid not null references tenant_erp_objects(id) on delete cascade,
    column_name text not null,
    data_type text,
    business_name text,
    display_name_tr text,
    description text,
    semantic_meaning_tr text,
    is_sensitive boolean not null default false,
    is_filterable boolean not null default true,
    is_groupable boolean not null default true,
    is_summable boolean not null default false,
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists tenant_erp_column_meanings (
    id uuid primary key default gen_random_uuid(),
    column_id uuid not null references tenant_erp_object_columns(id) on delete cascade,
    customer_label text not null,
    customer_description text,
    language_code text not null default 'tr',
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists tenant_erp_object_relationships (
    id uuid primary key default gen_random_uuid(),
    tenant_id uuid not null references tenants(id) on delete cascade,
    parent_object_id uuid not null references tenant_erp_objects(id) on delete cascade,
    child_object_id uuid not null references tenant_erp_objects(id) on delete cascade,
    parent_column_name text not null,
    child_column_name text not null,
    relationship_type text not null default 'one_to_many',
    description text,
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists tenant_erp_relations (
    id uuid primary key default gen_random_uuid(),
    tenant_id uuid not null references tenants(id) on delete cascade,
    relation_name text not null,
    source_view_id uuid not null references tenant_erp_objects(id) on delete cascade,
    target_view_id uuid not null references tenant_erp_objects(id) on delete cascade,
    join_type text not null default 'INNER JOIN',
    description_tr text,
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists tenant_erp_relation_columns (
    id uuid primary key default gen_random_uuid(),
    relation_id uuid not null references tenant_erp_relations(id) on delete cascade,
    source_column_name text not null,
    target_column_name text not null,
    ordinal integer not null default 1,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
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

create index if not exists idx_mobile_login_sessions_user_created_at
    on mobile_login_sessions (user_id, created_at desc);

create index if not exists idx_mobile_login_sessions_tenant_created_at
    on mobile_login_sessions (tenant_id, created_at desc);

create index if not exists idx_mobile_login_sessions_active_token
    on mobile_login_sessions (access_token_hash)
    where revoked_at is null;

create unique index if not exists idx_tenant_db_connections_active_tenant
    on tenant_db_connections (tenant_id)
    where is_active = true;

create unique index if not exists idx_tenant_erp_objects_tenant_object
    on tenant_erp_objects (tenant_id, object_name);

create index if not exists idx_tenant_erp_objects_domain
    on tenant_erp_objects (tenant_id, business_domain)
    where is_active = true and is_queryable = true;

create unique index if not exists idx_tenant_erp_object_columns_object_column
    on tenant_erp_object_columns (object_id, column_name);

create unique index if not exists idx_tenant_erp_column_meanings_column_language
    on tenant_erp_column_meanings (column_id, language_code);

create unique index if not exists idx_tenant_erp_object_relationships_unique
    on tenant_erp_object_relationships (
        tenant_id,
        parent_object_id,
        child_object_id,
        parent_column_name,
        child_column_name
    );

create unique index if not exists idx_tenant_erp_relations_tenant_name
    on tenant_erp_relations (tenant_id, relation_name);

create index if not exists idx_tenant_erp_relations_source_target
    on tenant_erp_relations (tenant_id, source_view_id, target_view_id)
    where is_active = true;

create unique index if not exists idx_tenant_erp_relation_columns_ordinal
    on tenant_erp_relation_columns (relation_id, ordinal);

create unique index if not exists idx_tenant_erp_relation_columns_pair
    on tenant_erp_relation_columns (relation_id, source_column_name, target_column_name);

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
    add column if not exists provider text not null default 'postgresql';

do $$
begin
    if exists (
        select 1
        from information_schema.columns
        where table_name = 'tenant_db_connections'
          and column_name = 'encrypted_password'
    ) and not exists (
        select 1
        from information_schema.columns
        where table_name = 'tenant_db_connections'
          and column_name = 'password_value'
    ) then
        alter table tenant_db_connections
            rename column encrypted_password to password_value;
    end if;
end $$;

alter table tenant_db_connections
    drop constraint if exists chk_tenant_db_connections_port;

alter table tenant_db_connections
    add constraint chk_tenant_db_connections_port
    check (port between 1 and 65535);

alter table tenant_db_connections
    drop constraint if exists chk_tenant_db_connections_provider;

alter table tenant_db_connections
    add constraint chk_tenant_db_connections_provider
    check (provider in ('postgresql', 'oracle', 'sqlserver', 'mysql'));

alter table tenant_erp_objects
    add column if not exists display_name_tr text;

alter table tenant_erp_objects
    add column if not exists description_tr text;

alter table tenant_erp_object_columns
    add column if not exists display_name_tr text;

alter table tenant_erp_object_columns
    add column if not exists semantic_meaning_tr text;

alter table tenant_erp_object_columns
    add column if not exists is_summable boolean not null default false;

alter table tenant_erp_objects
    drop constraint if exists chk_tenant_erp_objects_type;

alter table tenant_erp_objects
    add constraint chk_tenant_erp_objects_type
    check (object_type in ('table', 'view', 'materialized_view', 'synonym'));

alter table tenant_erp_object_relationships
    drop constraint if exists chk_tenant_erp_object_relationships_type;

alter table tenant_erp_object_relationships
    add constraint chk_tenant_erp_object_relationships_type
    check (relationship_type in ('one_to_one', 'one_to_many', 'many_to_one', 'many_to_many'));

alter table tenant_erp_relations
    drop constraint if exists chk_tenant_erp_relations_join_type;

alter table tenant_erp_relations
    add constraint chk_tenant_erp_relations_join_type
    check (join_type in ('INNER JOIN', 'LEFT JOIN', 'RIGHT JOIN', 'FULL JOIN'));

alter table tenant_erp_relation_columns
    drop constraint if exists chk_tenant_erp_relation_columns_ordinal;

alter table tenant_erp_relation_columns
    add constraint chk_tenant_erp_relation_columns_ordinal
    check (ordinal > 0);

with legacy_relation_groups as (
    select
        rel.tenant_id,
        lower(parent_object.object_name || '_to_' || child_object.object_name) as relation_name,
        rel.parent_object_id as source_view_id,
        rel.child_object_id as target_view_id,
        'INNER JOIN' as join_type,
        coalesce(max(rel.description), 'Migrated legacy view relationship.') as description_tr
    from tenant_erp_object_relationships rel
    join tenant_erp_objects parent_object on parent_object.id = rel.parent_object_id
    join tenant_erp_objects child_object on child_object.id = rel.child_object_id
    where rel.is_active = true
    group by rel.tenant_id, parent_object.object_name, child_object.object_name, rel.parent_object_id, rel.child_object_id
)
insert into tenant_erp_relations (
    tenant_id,
    relation_name,
    source_view_id,
    target_view_id,
    join_type,
    description_tr
)
select
    tenant_id,
    relation_name,
    source_view_id,
    target_view_id,
    join_type,
    description_tr
from legacy_relation_groups
on conflict (tenant_id, relation_name) do nothing;

with legacy_relation_columns as (
    select
        relation.id as relation_id,
        rel.parent_column_name as source_column_name,
        rel.child_column_name as target_column_name,
        row_number() over (
            partition by relation.id
            order by rel.parent_column_name, rel.child_column_name
        ) as ordinal
    from tenant_erp_object_relationships rel
    join tenant_erp_objects parent_object on parent_object.id = rel.parent_object_id
    join tenant_erp_objects child_object on child_object.id = rel.child_object_id
    join tenant_erp_relations relation
      on relation.tenant_id = rel.tenant_id
     and relation.relation_name = lower(parent_object.object_name || '_to_' || child_object.object_name)
    where rel.is_active = true
)
insert into tenant_erp_relation_columns (
    relation_id,
    source_column_name,
    target_column_name,
    ordinal
)
select
    relation_id,
    source_column_name,
    target_column_name,
    ordinal
from legacy_relation_columns
on conflict do nothing;
