# Security

Please report a security problem in Passenger privately, never in a public issue: use **Report a vulnerability** on the
repository's Security tab (https://github.com/Zaneris/passenger/security/advisories/new). Only you and the maintainer see
the report while it is worked on.

Say what you found, the version or commit, and the steps that show it.

## What to expect

- An answer within a few days saying whether the problem is confirmed, how severe it is taken to be and which versions
  it affects.
- A fix in a release: the next one for most problems, a patch release of its own for a severe one in a version people
  use.
- The advisory published as that release comes out, crediting you unless you'd rather not be named, with a CVE where it
  warrants one.
- No deadline is set on you; ninety days is the usual most, and a problem being exploited is fixed at once.

## How a fix is released (maintainers)

1. Confirm the report in its advisory: severity, affected versions, a CVE asked for where it warrants one.
2. A problem only the unreleased branch has, or one of little reach, is fixed there as other work is, its commits saying
   what Passenger now does.
3. A severe one in a released version is fixed apart from the public branches (the advisory's private fork, or a local
   branch), from the release it affects, changing nothing else. Its tests run on the maintainer's machines, since a
   private fork runs no workflows.
4. The fix and its version's tag are pushed together; the Release workflow builds, signs and publishes the patch release,
   its release notes saying it is a security release.
5. The advisory is published as the release is, with the fixed version and the reporter's credit.
