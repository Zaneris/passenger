// The download for the visitor's system, and whether the latest release has it. The links work without this script:
// it only chooses the main button and says when a build is not out yet.
(() => {
    "use strict";

    const repository = "Zaneris/passenger";
    const releases = `https://github.com/${repository}/releases`;
    const builds = {
        "mac-arm64": { asset: "Passenger-macos-arm64.dmg", label: "macOS, Apple Silicon" },
        "mac-x64": { asset: "Passenger-macos-x64.dmg", label: "macOS, Intel" },
        "windows-x64": { asset: "Passenger-windows-x64.exe", label: "Windows x64" },
        "windows-arm64": { asset: "Passenger-windows-arm64.exe", label: "Windows Arm64" },
        "linux-x64": { asset: "Passenger-linux-x64.tar.gz", label: "Linux x64" },
        "linux-arm64": { asset: "Passenger-linux-arm64.tar.gz", label: "Linux Arm64" },
    };

    async function system() {
        const agent = navigator.userAgent;
        let os = /Windows/.test(agent) ? "windows"
            : /Macintosh|Mac OS X/.test(agent) ? "mac"
            : /Linux|X11/.test(agent) && !/Android/.test(agent) ? "linux"
            : null;
        // An iPad asks for the desktop site as a Mac; only it has a touch screen.
        if (os === "mac" && navigator.maxTouchPoints > 1) {
            os = null;
        }
        if (os === null) {
            return null;
        }
        // Every Mac browser's user agent says Intel, so a Mac is taken for Apple Silicon unless Chromium's hints say x86.
        let arch = os === "mac" || /aarch64|arm64/i.test(agent) ? "arm64" : "x64";
        try {
            const hints = await navigator.userAgentData?.getHighEntropyValues(["architecture"]);
            if (hints?.architecture === "arm") {
                arch = "arm64";
            } else if (hints?.architecture === "x86") {
                arch = "x64";
            }
        } catch {
            // No hints: keep the guess from the user agent.
        }
        return `${os}-${arch}`;
    }

    async function latest() {
        try {
            const response = await fetch(`https://api.github.com/repos/${repository}/releases/latest`, {
                headers: { Accept: "application/vnd.github+json" },
            });
            if (response.status === 404) {
                return { none: true };
            }
            if (!response.ok) {
                return null;
            }
            const release = await response.json();
            return { tag: release.tag_name, date: release.published_at, assets: new Set(release.assets.map(a => a.name)) };
        } catch {
            return null;
        }
    }

    // Words, then a link, built as nodes so a release's tag is only ever text.
    function say(element, words, linkText, href) {
        const link = document.createElement("a");
        link.href = href;
        link.textContent = linkText;
        element.replaceChildren(`${words} `, link, ".");
    }

    function unavailable(link, why) {
        const span = document.createElement("span");
        span.setAttribute("aria-disabled", "true");
        span.title = why;
        span.append(...link.childNodes);
        link.replaceWith(span);
    }

    async function main() {
        const [found, release] = await Promise.all([system(), latest()]);
        const build = found ? builds[found] : null;
        const primary = document.getElementById("primary-download");
        const note = document.getElementById("primary-note");
        const status = document.getElementById("release-status");
        const linuxStatus = document.getElementById("linux-status");

        if (release?.none) {
            const words = "There is no release yet: Passenger is in early development. Until the first one, it can be built from source.";
            if (status) {
                say(status, words, "How to build it", `https://github.com/${repository}#building`);
            }
            if (linuxStatus) {
                linuxStatus.textContent = "There is no release yet, so these steps are for when the first Linux build is out.";
            }
            document.querySelectorAll("a[data-asset]").forEach(link => unavailable(link, "Not released yet"));
            if (primary && note) {
                primary.href = "#download";
                note.textContent = "No release yet. Passenger can be built from source today.";
            }
            return;
        }

        if (release) {
            const date = new Date(release.date).toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
            if (status) {
                say(status, `Passenger ${release.tag}, released ${date}.`, "Release notes", `${releases}/tag/${encodeURIComponent(release.tag)}`);
            }
            document.querySelectorAll("a[data-asset]").forEach(link => {
                if (!release.assets.has(link.dataset.asset)) {
                    unavailable(link, `Not in ${release.tag}`);
                }
            });
            if (linuxStatus && !release.assets.has(builds["linux-x64"].asset) && !release.assets.has(builds["linux-arm64"].asset)) {
                linuxStatus.textContent = `${release.tag} has no Linux build, so these steps are for when one is out.`;
            }
        }

        if (primary && note && build && (!release || release.assets.has(build.asset))) {
            primary.href = `${releases}/latest/download/${build.asset}`;
            primary.innerHTML = `Download <small>for ${build.label}</small>`;
            note.innerHTML = found.startsWith("linux")
                ? `Then see <a href="/linux">how to install on Linux</a>. <a href="#download">Other systems</a>.`
                : `<a href="#download">Other systems and architectures</a>`;
        }
    }

    main();
})();

// A Copy button on each of the agents page's setup commands and paths, so a setup line is taken whole and exact.
(() => {
    "use strict";

    function copy(text, button) {
        const done = () => {
            button.textContent = "Copied";
            setTimeout(() => { button.textContent = "Copy"; }, 1500);
        };
        if (navigator.clipboard?.writeText) {
            navigator.clipboard.writeText(text).then(done, () => { button.textContent = "Select and copy"; });
        } else {
            button.textContent = "Select and copy";
        }
    }

    function button(label, text) {
        const b = document.createElement("button");
        b.type = "button";
        b.className = "copy";
        b.textContent = "Copy";
        b.setAttribute("aria-label", `Copy ${label}`);
        b.addEventListener("click", () => copy(text(), b));
        return b;
    }

    document.querySelectorAll(".setup pre").forEach(pre => {
        const box = document.createElement("div");
        box.className = "copyable";
        pre.replaceWith(box);
        box.append(pre, button("the command", () => pre.textContent.trim()));
    });
    document.querySelectorAll(".setup .paths li").forEach(item => {
        const code = item.querySelector("code");
        if (code) {
            item.append(button(`${item.querySelector("span")?.textContent ?? ""} path`.trim(), () => code.textContent.trim()));
        }
    });
})();
