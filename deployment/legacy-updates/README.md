# Legacy client update migration

The legacy website feed advertises the existing signed Snow Shot **1.3.5**
Windows x64 release. `latest-version.json` is an unchanged copy of the Gitee
release asset, not a newly signed or edited payload:

https://gitee.com/mg-chao/snow-apps/releases/download/v1.3.5_snow-shot/latest-version.json

SHA-256: `189378c3894ed5d9c2a5476e30dfa5d8987782fbbbe9eb35c214bad10e06e1c2`.
The signature was verified against the public key retained in the client's
`v1.1.7-beta` tag. Package sizes and SHA-256 sidecars match Gitee's five x64
release assets. The metadata is 11,502 bytes, replacing a 513,282-byte feed for
`1.1.7-beta`.

Install `../nginx/snowshot-legacy-updates.conf` as
`/etc/nginx/snowshot-legacy-updates.conf`, included inside the HTTPS server
before other regex locations. Copy the JSON and text feeds to `/var/www/html`.
Known legacy package URLs return a non-cacheable HTTP 302 to the corresponding
versioned Gitee asset. Unknown historical archive/installer names redirect to
the release page. This offloads package transfers to Gitee.

The historical Windows updater restricts cross-origin redirects. It can verify
and display the new signed version, but its automatic downloader may reject
the Gitee redirect. Browser downloads support the redirect; affected users
must install the new version manually. Do not describe these redirects as a
working automatic-update transport for those binaries.

For a later migration, verify the new published signed feed and package
sidecars, update both feed files and the pinned version in the Nginx snippet,
and preserve a backup of the live feeds and configuration. Run `nginx -t`
before reloading. Then verify public JSON signature/version, text version,
HTTP 302 targets, and `/health/ready`.

## Production verification on 22 October 2026

Deployed at **10:27:34 Asia/Shanghai**. The prior live feeds and Nginx
configuration are retained at
`/root/snowshot-legacy-update-backups/20261022T022734Z` on the API host.

`nginx -t` and the configuration reload succeeded. Public checks verified the
exact signed JSON bytes, the `1.3.5` text feed, all 22 tested download/sidecar
redirects, and live/ready API responses from `policy-17-c13a440`. Following the
online installer redirect reached Gitee's download host with HTTP 200 and the
signed 27,006,323-byte package size. Gzip metadata is 4,708 bytes; identity
metadata is 11,502 bytes, about 97.8% smaller than the prior identity feed.
