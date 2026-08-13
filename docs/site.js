/* Deckhand documentation — the small amount of script the pages want.

   Four jobs, none of them load-bearing: the pages read fine with scripting off.
     1. Highlight the JSON and PowerShell samples.
     2. Put a copy button on every sample.
     3. Build the "on this page" list from the headings, so the markup stays
        free of a table of contents that has to be kept in step by hand.
     4. Remember a light/dark choice, which otherwise follows the system.  */

(function () {
  "use strict";

  // ---- Theme -----------------------------------------------------------

  var STORE = "deckhand.docs.theme";

  function applyTheme(mode) {
    if (mode === "dark" || mode === "light") {
      document.documentElement.setAttribute("data-theme", mode);
    } else {
      document.documentElement.removeAttribute("data-theme");
    }
  }

  function currentTheme() {
    return document.documentElement.getAttribute("data-theme")
      || (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
  }

  try { applyTheme(localStorage.getItem(STORE)); } catch (e) { /* private mode */ }

  function wireTheme() {
    var button = document.getElementById("theme");
    if (!button) return;

    function label() {
      button.textContent = currentTheme() === "dark" ? "◐  Light theme" : "◐  Dark theme";
    }

    button.addEventListener("click", function () {
      var next = currentTheme() === "dark" ? "light" : "dark";
      applyTheme(next);
      try { localStorage.setItem(STORE, next); } catch (e) { /* ignore */ }
      label();
    });

    label();
  }

  // ---- Highlighting ----------------------------------------------------

  function escapeHtml(text) {
    return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  }

  // One pass, so a quoted string inside a comment can't be picked up as a
  // string: whichever alternative matches at a position consumes it.
  var JSON_TOKENS = new RegExp([
    "(//[^\\n]*)",                       // comment
    "(\"(?:[^\"\\\\]|\\\\.)*\"\\s*:)",   // key, colon included
    "(\"(?:[^\"\\\\]|\\\\.)*\")",        // string
    "\\b(true|false|null)\\b",           // literal
    "(-?\\d+(?:\\.\\d+)?)"               // number
  ].join("|"), "g");

  function highlightJson(code) {
    return escapeHtml(code).replace(JSON_TOKENS,
      function (match, comment, key, string, literal, number) {
        if (comment) return '<span class="tok-comment">' + comment + "</span>";
        if (key) return '<span class="tok-key">' + key + "</span>";
        if (string) return '<span class="tok-string">' + string + "</span>";
        if (literal) return '<span class="tok-bool">' + literal + "</span>";
        if (number) return '<span class="tok-number">' + number + "</span>";
        return match;
      });
  }

  function highlightShell(code) {
    return escapeHtml(code)
      .replace(/(^|\n)(\s*)(#[^\n]*)/g, "$1$2" + '<span class="tok-comment">$3</span>')
      .replace(/(--?[a-zA-Z][\w-]*)/g, '<span class="tok-bool">$1</span>');
  }

  function highlightAll() {
    var blocks = document.querySelectorAll("pre > code");
    Array.prototype.forEach.call(blocks, function (block) {
      var source = block.textContent;
      if (block.classList.contains("lang-json")) {
        block.innerHTML = highlightJson(source);
      } else if (block.classList.contains("lang-shell")) {
        block.innerHTML = highlightShell(source);
      }
      block.setAttribute("data-source", source);
    });
  }

  // ---- Copy buttons ----------------------------------------------------

  function addCopyButtons() {
    var figures = document.querySelectorAll("figure.code");
    Array.prototype.forEach.call(figures, function (figure) {
      var code = figure.querySelector("pre > code");
      if (!code) return;

      var button = document.createElement("button");
      button.type = "button";
      button.className = "copy";
      button.textContent = "Copy";
      button.setAttribute("aria-label", "Copy this example to the clipboard");

      button.addEventListener("click", function () {
        var text = code.getAttribute("data-source") || code.textContent;

        function done() {
          button.textContent = "Copied";
          button.classList.add("done");
          setTimeout(function () {
            button.textContent = "Copy";
            button.classList.remove("done");
          }, 1400);
        }

        // navigator.clipboard needs a secure context, which a file:// page is
        // not, so the old selection route is the one that actually runs here.
        if (navigator.clipboard && window.isSecureContext) {
          navigator.clipboard.writeText(text).then(done, fallback);
        } else {
          fallback();
        }

        function fallback() {
          var scratch = document.createElement("textarea");
          scratch.value = text;
          scratch.setAttribute("readonly", "");
          scratch.style.position = "fixed";
          scratch.style.left = "-9999px";
          document.body.appendChild(scratch);
          scratch.select();
          try { document.execCommand("copy"); done(); }
          catch (e) { button.textContent = "Ctrl+C"; }
          document.body.removeChild(scratch);
        }
      });

      figure.appendChild(button);
    });
  }

  // ---- On this page ----------------------------------------------------

  function buildToc() {
    var list = document.getElementById("toc");
    if (!list) return;

    var headings = document.querySelectorAll("main h2[id], main h3[id]");
    if (!headings.length) {
      var label = document.querySelector(".nav-label");
      if (label) label.style.display = "none";
      return;
    }

    var links = [];
    Array.prototype.forEach.call(headings, function (heading) {
      var item = document.createElement("li");
      var link = document.createElement("a");
      link.href = "#" + heading.id;
      link.textContent = heading.getAttribute("data-toc") || heading.textContent;
      if (heading.tagName === "H3") link.className = "sub";
      item.appendChild(link);
      list.appendChild(item);
      links.push({ link: link, heading: heading });
    });

    // Mark whichever heading is nearest the top of the viewport. A scroll
    // listener rather than IntersectionObserver: the headings are short and
    // this has to answer "which one am I under", not "which are visible".
    var ticking = false;
    function mark() {
      ticking = false;
      var best = links[0];
      for (var i = 0; i < links.length; i++) {
        if (links[i].heading.getBoundingClientRect().top <= 120) best = links[i];
      }
      links.forEach(function (entry) {
        entry.link.classList.toggle("here", entry === best);
      });
    }

    window.addEventListener("scroll", function () {
      if (ticking) return;
      ticking = true;
      window.requestAnimationFrame(mark);
    }, { passive: true });

    mark();
  }

  // ---- Go --------------------------------------------------------------

  function start() {
    wireTheme();
    highlightAll();
    addCopyButtons();
    buildToc();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", start);
  } else {
    start();
  }
})();
