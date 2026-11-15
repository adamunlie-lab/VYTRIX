# Turning on VYTRIX accounts

VYTRIX accounts let people sign in and keep their setup in sync across PCs. They run on a **Supabase** project that you own. Supabase has a free tier and provides sign-in and the database, so you don't run a server yourself.

Until steps 1–5 are done, the Account page says accounts aren't switched on yet, and the rest of VYTRIX works as normal.

## 1. Create the Supabase project

1. Sign up at supabase.com and create a new project. Choose a region near your users and a strong database password, and keep that password to yourself.
2. Open **Project Settings → API** and note:
   - the **Project URL**, which looks like `https://abcdefgh.supabase.co`
   - the **anon / public** key

   The anon key is meant to be public. Never use the `service_role` key in the app.

## 2. Create the database table

Open **SQL Editor → New query**, paste the contents of `server/supabase/schema.sql`, and click **Run**. This does three things:

- It creates the `vytrix_sync` table, one row per account.
- It turns on Row Level Security, so each person can only read, write or delete their own row.
- It adds `delete_my_account()`, so people can delete their own account from the app.

## 3. Allow VYTRIX's sign-in redirect

Go to **Authentication → URL Configuration → Redirect URLs** and add:

```
http://127.0.0.1:*/**
```

VYTRIX signs in through the browser and receives the result on a temporary local address (any free port), using PKCE. Nothing is exposed to the internet.

## 4. Turn on sign-in methods

Go to **Authentication → Sign In / Providers**. VYTRIX automatically shows a button for each method you enable:

| Method | What you need |
|---|---|
| Email | On by default. Decide whether new users must confirm their email address; confirming is recommended. |
| Google | A Google Cloud OAuth client (type: Web application). Use the callback URL Supabase shows you as the authorized redirect URI. |
| Microsoft | An app registration in Microsoft Entra (Azure). Supabase calls this provider **Azure**. Use the callback URL Supabase shows you. |
| GitHub | A GitHub OAuth App. Use the callback URL Supabase shows you. |
| Discord | A Discord application with OAuth2. Use the callback URL Supabase shows you. |

Each provider's page in Supabase walks you through its setup. These accounts are yours, so only you can create them.

## 5. Put the project into VYTRIX

In `src/Vytrix/Services/Accounts.cs`, set:

```csharp
public const string SupabaseUrl = "https://YOUR-PROJECT.supabase.co";
public const string SupabaseAnonKey = "YOUR-ANON-KEY";
```

Then publish a new version. For testing without rebuilding, the environment variables `VYTRIX_ACCOUNTS_URL` and `VYTRIX_ACCOUNTS_KEY` override these values.

## What's stored, and your responsibilities

- **What VYTRIX stores:** each account's email, whatever name and picture the sign-in provider shares, and the setup the person chooses to save. That's which tweaks are on, their Custom profile, appearance settings, and a label naming their graphics card.
- **What it never stores:** files, passwords, computer names or Assistant conversations.
- **Deletion:** people can delete their synced data or their whole account from the Account page.
- **Your role:** as the project owner, you control this data. The Terms of Agreement (section 6(i)) tell users what's stored. If you distribute VYTRIX widely, consider a privacy policy and local data-protection rules, and get legal advice if you're unsure.
