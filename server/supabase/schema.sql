-- VYTRIX accounts: run this once in your Supabase project (SQL Editor -> New query -> Run).
-- It stores one synced setup per account. Row Level Security means every person can only
-- ever read, write or delete their own row, even though the app ships with the public anon key.

create table if not exists public.vytrix_sync (
    user_id    uuid primary key references auth.users (id) on delete cascade,
    data       jsonb not null,
    device     text not null default '',
    updated_at timestamptz not null default now()
);

-- Keep payloads small and well-formed: a setup is a few kilobytes.
alter table public.vytrix_sync drop constraint if exists vytrix_sync_size;
alter table public.vytrix_sync add constraint vytrix_sync_size check (pg_column_size(data) < 65536 and char_length(device) <= 80);

alter table public.vytrix_sync enable row level security;

drop policy if exists "read own setup"   on public.vytrix_sync;
drop policy if exists "insert own setup" on public.vytrix_sync;
drop policy if exists "update own setup" on public.vytrix_sync;
drop policy if exists "delete own setup" on public.vytrix_sync;

create policy "read own setup"   on public.vytrix_sync for select to authenticated using ((select auth.uid()) = user_id);
create policy "insert own setup" on public.vytrix_sync for insert to authenticated with check ((select auth.uid()) = user_id);
create policy "update own setup" on public.vytrix_sync for update to authenticated using ((select auth.uid()) = user_id) with check ((select auth.uid()) = user_id);
create policy "delete own setup" on public.vytrix_sync for delete to authenticated using ((select auth.uid()) = user_id);

-- The anonymous (signed-out) role gets nothing.
revoke all on public.vytrix_sync from anon;
grant select, insert, update, delete on public.vytrix_sync to authenticated;

-- Lets a signed-in person delete their own account (and, through the cascade, their synced setup).
create or replace function public.delete_my_account()
returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if auth.uid() is null then
        raise exception 'Not signed in';
    end if;
    delete from auth.users where id = auth.uid();
end;
$$;

revoke all on function public.delete_my_account() from public, anon;
grant execute on function public.delete_my_account() to authenticated;
