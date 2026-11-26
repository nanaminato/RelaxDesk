# Internal file transfer resume

The file engine retains bounded chunk/resume primitives for scoped transfers. They are internal transport facilities, not a standalone transfer UI or permission model. The Viewer enables them only when the current Host advertises support and a valid current scope. Live clipboard-source transfers may disable resume because their source binding is scoped to the current clipboard offer.

The Viewer discards its copy queue across connection attempts. After an interruption, reconnect and copy the files again. There is no cross-session continue prompt, per-file reapproval, persisted feature grant or old-version migration. Every operation still validates its current session, source/destination identity, bounds and integrity before IO. Full disconnect joins workers and closes handles.
