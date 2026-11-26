# Remembered usernames independent of passwords

Successful password login now records the username against canonical IP/port
in bounded Viewer preferences (at most 20 endpoints). It does so regardless of
the Remember credentials checkbox. Unchecked successful login deletes any
previous password entry for that endpoint. Endpoint changes fill the remembered
username first; a password is filled only for a matching separately opted-in
Credential Manager entry. Editing or failed login does not record a username.

Validation with stable .NET SDK 11.0.100:

- Release solution build with `--artifacts-path E:\RelaxDesk\build\username-update`:
  passed, zero warnings/errors.
- Built Viewer `--credential-contracts`: 24 passed checks, including real
  random-scope Credential Manager entries, successful unchecked username save,
  actual preference-file reload and new-window autofill, empty password,
  IP/port isolation and unsaved username edits.
- Built Viewer `--preferences-contracts E:\RelaxDesk\build\username-preferences-fixtures`:
  21 passed preference/history/video restoration and validation checks.

Tests use synthetic usernames, passwords and local files; no actual user login
or physical dual-PC authentication is claimed. No performance measurement.
