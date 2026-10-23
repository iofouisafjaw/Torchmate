# Security and local data

Use the project's supported loopback endpoint. The standard launcher verifies the
brain version and actual model file and does not stop unrelated services. This is a
single-player prototype; multiplayer authority and adversarial mod combinations
have not been accepted as supported configurations.

Please report a reproducible defect with a minimal synthetic example and a short,
redacted receipt. Do not post credentials, `.env`, full chat histories, player/world
saves or private filesystem paths in a public issue. No private vulnerability-reporting
channel has been configured yet; do not publish exploit details or secrets while
trying to contact a maintainer.

The source export omits models, game files, Wiki databases, recordings, runtime data
and inherited Git history. Its static scanner is an additional check, not a guarantee
that arbitrary future edits cannot contain secrets. Never push the original private
checkout as a substitute for reviewing the prepared snapshot.
