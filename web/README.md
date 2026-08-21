# CaseMesh Web

The Web MVP is a same-origin Next.js client for `CaseMesh.Api`. Configure the API with external secrets; do not commit them.

Required production configuration uses the `CaseMesh__` environment prefix:

- `PostgresConnectionString`
- `PublicOrigin` (HTTPS)
- `OidcAuthority`, `OidcClientId`, `OidcClientSecret`
- `S3Endpoint` (HTTPS), `S3Region`, `S3BucketName`, `S3AccessKey`, `S3SecretKey`

Set `CASEMESH_API_INTERNAL_ORIGIN` for the Next.js server-side `/api` rewrite. `EnableTestAuthentication` is accepted only with `ASPNETCORE_ENVIRONMENT=Testing` and must never be enabled in production.

Run `npm run lint`, `npm test`, and `npm run build`. The Playwright journey additionally requires the real PostgreSQL and S3-compatible services plus the API and Web servers.
