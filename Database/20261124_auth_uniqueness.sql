-- Authentication treats usernames/emails case-insensitively. Enforce the same
-- uniqueness in PostgreSQL, including concurrent requests. No users are changed.
-- If existing case-insensitive duplicates exist, creation fails and rolls back;
-- review those accounts instead of deleting/merging identities automatically.
BEGIN;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Users_Username_CaseInsensitive"
    ON public."Users" (LOWER("Username"));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Users_Email_CaseInsensitive"
    ON public."Users" (LOWER("Email"));
COMMIT;
