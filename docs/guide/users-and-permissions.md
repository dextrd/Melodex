# Users and permissions

## Users

The admin, made at setup, may do everything. Add other people under **Settings → Users**, giving each only what they
need. Nobody can give a permission they don't have, and only the admin can change the admin's account. Removing
someone signs them out and revokes their API keys; what they asked for stays.

Each user's card lists what they may do; click the list to see all of it. Its settings button opens their account in
three tabs, saved together:

- **Login**: their username, and a new password, which signs them out everywhere.
- **Permissions**: what they may do.
- **Media server accounts**, when a media server that's set up syncs playlists: their login on each, to sync their
  playlists. The one in use is marked; the others' logins are kept for switching to them. Each login is checked with
  its server when you save, and a tab with something to fix is marked.

Anyone can change their own account under **Your account**, in the account menu at the top right: the same three
tabs, for themselves. Changing their username or password needs their current password, and a new password signs
them out everywhere else.

## What each permission allows

| Permission | Allows |
| --- | --- |
| **Browse** | Seeing the library, the catalogue, what's being fetched and what's running |
| **Request** | Asking for music to be fetched into the library, and ignoring albums and songs; looking for upgrades, approving or rolling them back, replacing files that seem lossy, and keeping a copy turned away for its quality |
| **Listen** | Playing music, fetching songs to listen to without adding them, and having playlists |
| **Review and match** | Approving, matching, excluding, deferring and hiding files on Review, and trusting files that seem lossy to be lossless |
| **Format** | Rewriting files' tags and covers, and moving them; choosing album and song covers; looking for, choosing, editing and deleting a song's lyrics |
| **Remove music** | Deleting music from the library, or hiding it |
| **Scan** | Scanning the library, and pausing, cancelling and reordering tasks |
| **Settings and plugins** | Changing settings and plugins, and reading the log |
| **API keys and webhooks** | Making API keys for apps, and managing webhooks |
| **Manage users** | Adding, changing and removing other users |

Someone who just listens needs Browse and Listen. Buttons for what someone may not do don't show.

## API keys

Apps use a key from **Settings → API**. A key belongs to whoever made it and can never do more than they may: if their
permissions shrink, or they're removed, so does the key. **Everything you may do** gives a key all its owner's
permissions, now and later. See the [client guide](../client-guide.md) for the API itself.

## A lost admin password

Start Melodex once with `Melodex__ResetLogin=true`, open the web app and set a new password, then remove the variable
and restart. Other users, and everyone's keys, are kept. Anyone else who forgets theirs can have a new one set by
someone who may manage users.

Next: [Appearance](appearance.md).
