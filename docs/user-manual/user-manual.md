# LLM Wiki — User Manual

Welcome. This guide walks you through the LLM Wiki app: what it is, how to open it, and how
to use each of its four screens. It is written for everyday use, so you do not need to know
anything about the code or the database underneath to follow along. If you run into a word you
have not seen before, check the [Glossary](#glossary) at the end.

---

## Contents

1. [What is LLM Wiki?](#what-is-llm-wiki)
2. [Before you start](#before-you-start)
3. [Getting the app open](#getting-the-app-open)
4. [The layout at a glance](#the-layout-at-a-glance)
5. [The Chat tab: ask questions](#the-chat-tab-ask-questions)
6. [The Browse tab: read the wiki](#the-browse-tab-read-the-wiki)
7. [The Projects tab: manage your wikis](#the-projects-tab-manage-your-wikis)
8. [The Status tab: check everything is working](#the-status-tab-check-everything-is-working)
9. [Adding new material (ingestion)](#adding-new-material-ingestion)
10. [Tips and good habits](#tips-and-good-habits)
11. [Troubleshooting](#troubleshooting)
12. [Glossary](#glossary)

---

## What is LLM Wiki?

LLM Wiki is a personal knowledge base that you can talk to. You feed it documents, and it turns
them into a tidy collection of linked pages. After that, instead of hunting through files, you
simply ask a question in plain English and the app writes back a short answer, along with links
to the exact pages it drew from.

Three ideas make it work:

* **Your documents become pages.** When you add a source, the app reads it and writes a set of
  organised pages: summaries, entities (the people, tools, and things it found), concepts, and
  broader topic overviews.
* **Every answer is grounded.** The app never makes things up on its own. It searches your pages,
  reads the best matches, and then answers using only what it found. Each answer shows its sources
  so you can check them yourself.
* **It is honest about gaps.** If your wiki does not cover a question, the app tells you plainly
  rather than guessing.

Everything you see lives in one place called a **project**. A project and a wiki are the same
thing: one named collection of pages. You can have several projects side by side (say, one for
work notes and one for a hobby) and switch between them.

---

## Before you start

The app has two parts that need to be running:

1. **The server (the API).** This is the engine that stores pages, runs the searches, and talks
   to the language model. It usually runs at `http://localhost:5080`.
2. **The app itself (the web client).** This is the screen you interact with, which opens in your
   web browser.

If someone has already set these up for you, you can skip straight to the next section. If you are
setting things up yourself, the project's main [README](../../README.md) has the full step by step
for starting the server and the supporting services.

> **A quick note on privacy.** When configured to run with a local model, the app works entirely on
> your own machine and does not send your documents to any outside service.

---

## Getting the app open

Once the server is running, start the app and open it in your browser. From a terminal, in the
project folder:

```bash
cd app
npm install      # first time only
npm run web
```

Then open the address it prints, usually `http://localhost:8081`. That is it. The app loads and
you are looking at the Chat screen.

---

## The layout at a glance

The app is deliberately simple. Along the bottom you will always see four buttons:

| Tab | What it is for |
| --- | --- |
| **Chat** | Ask questions and get grounded, cited answers. |
| **Browse** | Read through the pages in your wiki by category. |
| **Projects** | See, create, switch between, and remove your wikis. |
| **Status** | Confirm the app and its services are healthy. |

The tab you are currently on is shown in blue with an underline. Tap any button to switch. The name
of the wiki you are working in appears at the top of the Chat and Browse screens, so you always know
where you are.

---

## The Chat tab: ask questions

This is the heart of the app. When you first open it, the screen is mostly empty and invites you to
ask something about the current wiki.

![The empty Chat screen, ready for a question](../images/Screenshot%20from%202026-07-12%2013-24-26.png)

### Asking a question

Type your question into the box at the bottom and press **Send** (or the Enter key). Ask in normal
language, the way you would ask a colleague. For example: *"What is React Native?"*

While the app works, it shows a spinner and the word **Thinking…**. This is normal. It is searching
your pages, reading the best ones, and writing an answer. This can take a few seconds.

![The Chat screen showing a question sent and a "Thinking…" spinner](../images/Screenshot%20from%202026-07-12%2013-26-37.png)

### Reading the answer

When it is ready, the answer appears in a card. Below the text you will see one or more **source
chips**. Each chip names a page the answer was built from and shows what kind of page it is, such
as **SUMMARY** or **OVERVIEW**.

![A finished answer with source chips and a Save answer button](../images/Screenshot%20from%202026-07-12%2013-27-07.png)

Tapping a source chip opens that page in a pop up so you can read it in full and judge the answer
for yourself. Here is the summary page behind the first chip:

![A source page opened in a pop up, showing a summary with key points](../images/Screenshot%20from%202026-07-12%2013-27-17.png)

And here is the overview page behind the second chip, with its related links listed:

![A second source page opened in a pop up, showing an overview](../images/Screenshot%20from%202026-07-12%2013-27-29.png)

Close the pop up with the **✕** in its top corner to return to the conversation.

### Following up

You do not have to repeat yourself. Ask a follow up question and the app remembers what you were
just talking about, so *"and how does it compare to the other one?"* works as expected.

### Saving a good answer

If an answer is genuinely useful, press **Save answer**. The app writes it back into your wiki as a
new page, so the next time you (or the app) search, that answer is part of the knowledge base. Over
time your wiki gets smarter the more you use it.

### When the wiki cannot help

If your pages do not cover the question, the app says so clearly instead of inventing an answer, and
it will not offer to save anything. That honesty is the point: an answer you can trust is worth more
than one that merely sounds confident.

---

## The Browse tab: read the wiki

Sometimes you want to read rather than ask. The Browse tab lists every page in the current wiki,
grouped by category (such as **CONCEPTS**, **SUMMARIES**, **ENTITIES**, and **TOPICS**).

![The Browse screen listing pages under the Concepts heading](../images/Screenshot%20from%202026-07-12%2013-24-53.png)

Scroll to explore the list. Tap any entry to open that page in a pop up and read it. To refresh the
list (for instance after adding new material), pull the list down and release.

![A page opened from the Browse list, showing a concept definition](../images/Screenshot%20from%202026-07-12%2013-28-18.png)

---

## The Projects tab: manage your wikis

A project is one wiki. The Projects tab is where you keep them.

![The Projects screen showing one active project card and a create box](../images/Screenshot%20from%202026-07-12%2013-25-10.png)

Each project is shown as a card with a few useful facts:

* A **star (★)** marks the project you are currently working in.
* The counts tell you how big it is, for example *94 pages · 13 sources*.
* **Last ingest** is the date you most recently added material to it.

### Switching projects

Tap a project card to make it the active one. From that moment, the Chat and Browse tabs work
against that wiki, and its name appears at the top of those screens.

### Creating a project

Type a name into the **New project name…** box and press **Create**. A fresh, empty wiki is set up
and becomes your active project. You add material to it through ingestion (see the next section).

### Deleting a project

Each card has a red **Delete** button. Use it with care: deleting a project removes the whole wiki,
its pages, and its saved sources, and this cannot be undone. The app asks you to confirm first. If
you delete the project you were working in, the app simply forgets which one was active and you pick
another.

---

## The Status tab: check everything is working

If something feels off, the Status tab is your first stop. It runs three quick checks and reports
whether each passed.

![The Status screen showing all three checks passing](../images/Screenshot%20from%202026-07-12%2013-25-24.png)

The three checks are:

1. **Oracle** — the database that stores the search index is reachable.
2. **Embedding** — the model that turns text into searchable numbers is working.
3. **Chat** — the language model that writes answers is responding.

When all three pass you will see a friendly **All systems go**. If one fails, its line explains what
went wrong, which points you at what to fix. Press **Re-run diagnostics** to check again after you
have made a change. The address of the server the app is talking to is shown near the top, so you
can confirm it is pointing where you expect.

---

## Adding new material (ingestion)

The app reads and answers questions in the browser, but adding new documents is done from the
command line. This is called **ingestion**. You point the tool at a file, and it reads that file and
grows your wiki from it: writing a summary, creating pages for the entities and concepts it found,
and noting anything that contradicts what is already there.

```bash
# Add a document to the active project
dotnet run --project src/LlmWiki.Cli -- ingest ./path/to/your-document.md

# Or name the wiki explicitly
dotnet run --project src/LlmWiki.Cli -- ingest my-wiki ./path/to/your-document.md
```

After it finishes, switch back to the app, open the **Browse** tab, and pull to refresh. Your new
pages will be there, and the **Chat** tab can answer questions about them straight away. The main
[README](../../README.md) covers ingestion and the other command line features in more depth.

---

## Tips and good habits

* **Ask one thing at a time.** Clear, focused questions get clearer answers.
* **Open the sources.** The source chips are there so you can verify an answer, not just trust it.
* **Save the answers you will want again.** A saved answer becomes part of the wiki and helps future
  searches.
* **Keep one project per subject.** Separate wikis stay tidy and their answers stay on topic.
* **Check the Status tab first.** If answers stop coming, a quick health check often shows the cause.

---

## Troubleshooting

**The app opens but nothing loads, or every action fails.**
The server is probably not running, or the app is pointing at the wrong address. Open the Status tab
and check the server address near the top. Make sure the server is started (see the README).

**I ask a question and it says the wiki does not cover it.**
The wiki has no pages on that subject yet. Add relevant documents through ingestion, then ask again.

**The Status tab shows a red check.**
Read the message on the failing line. An **Oracle** or **Embedding** failure usually means a
background service is not running. A **Chat** failure usually means the language model is not
configured or reachable. Fix the named part, then press **Re-run diagnostics**.

**My new document is not showing up.**
Confirm the ingestion command finished without errors, that you added it to the project you are
viewing, and then pull to refresh on the Browse tab.

**Answers are slow.**
The first question after starting up can take longer while things warm up. If it is running against
a local model, speed also depends on your computer. A few seconds of "Thinking…" is normal.

---

## Glossary

Plain explanations of the words you will meet while using the app.

**Answer page** — A page created when you press *Save answer*. It stores a useful answer so it
becomes part of your wiki and can be found in future searches.

**Chip (source chip)** — The small tappable button under an answer that names a page the answer drew
from. Tap it to read that page.

**Citation** — A pointer from an answer to the exact page it came from. Citations are what let you
check an answer for yourself.

**Concept** — A page describing an idea or term found in your documents, for example *Actions
(Redux)*.

**Diagnostics** — The set of quick health checks on the Status tab that confirm the app's services
are working.

**Embedding** — A way of turning a piece of text into a list of numbers that captures its meaning,
so the app can find pages that are *about* the same thing even when the exact words differ. You do
not interact with this directly; it powers search behind the scenes.

**Entity** — A page for a specific named thing found in your documents: a person, a tool, a company,
a product.

**Grounded answer** — An answer built only from your own pages, rather than from the model's general
knowledge. Grounded answers come with sources you can open.

**Ingestion** — The step where you add a document and the app reads it and grows your wiki from it.
Done from the command line.

**LLM (large language model)** — The kind of AI that reads and writes natural language. It is what
writes the answers in the Chat tab.

**Oracle** — The database used to store the search index. It runs in the background; you only notice
it on the Status tab.

**Overview / Topic** — A broader page that ties several related ideas together, giving the bigger
picture across a subject.

**Page** — A single markdown document in your wiki. Summaries, entities, concepts, overviews, and
saved answers are all pages.

**Project** — One named wiki. Switching projects switches which collection of pages the Chat and
Browse tabs work with. A project and a wiki are the same thing.

**Semantic search** — Searching by meaning rather than by matching exact words, so a question
phrased differently from the page still finds it.

**Source** — An original document you added to the wiki through ingestion. Your pages are built from
your sources.

**Summary** — A page giving a short, key point overview of a document you added.

**Wiki** — Your whole collection of linked pages. The same thing as a project.
