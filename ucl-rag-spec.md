# UCL Regulations RAG — Build Spec & Acceptance Rubric

A small, sharply-scoped retrieval system over the UEFA Champions League competition
regulations, with hand-built hybrid retrieval, one agentic tool call, and a real
evaluation harness. Built in .NET, Postgres/pgvector, and Azure OpenAI.

**This is deliberately small.** The differentiator is the evaluation harness and the
measured before/after experiment, not feature count. Do not expand scope.

---

## 1. Goal

Answer natural-language questions about the UEFA Champions League regulations with
citations to the specific paragraph, and correctly decline when the regulations do not
cover the question.

One agentic extension: questions needing both the rule text *and* squad data (e.g.
locally-trained quota compliance) trigger a tool call to a local roster dataset.

### Non-goals (do not build these)

- User accounts, authentication, or authorization
- Multi-turn conversation memory
- Multi-agent orchestration or agent graphs
- Streaming responses
- Cloud-hosted vector databases or managed search services
- Chunk reranking (an optional experiment only, see M4)
- Any scraping — the corpus is downloaded by hand

---

## 2. Fixed stack

| Layer | Choice |
|---|---|
| Language | C# / .NET (latest LTS) |
| API | ASP.NET Core minimal API |
| Store | Postgres 16 with the `pgvector` extension, via docker compose |
| Dense retrieval | pgvector cosine similarity |
| Sparse retrieval | Postgres full-text search (`tsvector` / `ts_rank`) |
| Fusion | Reciprocal rank fusion, hand-written |
| Embeddings + generation | Azure OpenAI |
| Agent / tool calling | Microsoft Agent Framework |
| Frontend | React + TypeScript, single page, Vite |
| CI | GitHub Actions |

**The fusion step is written by hand, not delegated to a library.** Combining the dense
and sparse rankings is the part of this project worth understanding, and M4's experiment
depends on being able to run each path in isolation.

Verify current package names and API surfaces against official docs before writing code —
the Microsoft Agent Framework and Azure OpenAI SDKs change often. In particular, confirm
Microsoft Agent Framework works against an Azure OpenAI endpoint in this configuration
before building M3 on it; if it does not, say so rather than working around it silently.

Secrets go in user-secrets locally and GitHub Actions secrets in CI. No keys in the repo.
Postgres runs locally in Docker with a committed `docker-compose.yml` — a clean clone plus
`docker compose up` plus an Azure OpenAI endpoint should be the whole setup.

---

## 3. Corpus

**Regulations of the UEFA Champions League, 2025/26 season (2024-27 cycle).**
Enforcement date 11 September 2025. One document. Downloaded by hand into `/docs`.

Reader and PDF download:
`https://documents.uefa.com/r/Regulations-of-the-UEFA-Champions-League-2025/26-Online`

The edition is **pinned on purpose**. The gold evaluation set carries paragraph-level
references read from this exact text. Do not substitute a different season — the
references will silently stop matching.

Structure worth exploiting: the document numbers every paragraph as `ARTICLE.PARAGRAPH`
(`31.04`, `6.01`), with sub-items lettered (`31.14(a)`). Ninety-six articles across
fifteen chapters, plus Annexes A–H. Chunks must retain that numbering — the paragraph
reference is what gets cited, so it has to survive ingestion. A chunk with no paragraph
reference is a bug.

---

## 4. Milestones & Definition of Done

Work through these in order. Each is independently demoable. Do not start a milestone
before the previous one's checklist fully passes.

### M1 — Ingestion & indexing

- [ ] `docker-compose.yml` brings up Postgres 16 with `pgvector` enabled
- [ ] Schema migration creates a `chunks` table with: `id`, `article_number`,
      `paragraph_number`, `article_title`, `chunk_text`, `embedding vector(N)`,
      `text_search tsvector`
- [ ] An HNSW or IVFFlat index on `embedding`, and a GIN index on `text_search`
- [ ] PDF parsed to text with article and paragraph structure preserved
- [ ] Chunks embedded via Azure OpenAI and inserted
- [ ] Ingestion is a re-runnable console command, not a one-off script
- [ ] Re-running ingestion is idempotent (no duplicate rows)
- [ ] Zero rows with a null or empty `paragraph_number`
- [ ] Spot-check: paragraph 18.01 (a ten-item ordered list) survives as one coherent chunk

### M2 — Retrieval & answering API

- [ ] Three retrieval functions, each independently callable and independently testable:
      `SearchDense(query, k)`, `SearchSparse(query, k)`, `SearchHybrid(query, k)`
- [ ] `SearchHybrid` combines the other two using reciprocal rank fusion, written by hand.
      The `k` constant in the RRF formula is a named configuration value, not a literal
      buried in the method
- [ ] Retrieval mode (`dense` / `sparse` / `hybrid`) is configurable at runtime — M4's
      experiment depends on switching it without a rebuild
- [ ] `POST /ask` accepts `{ "question": string }`
- [ ] Returns `{ "answer": string, "citations": [{ "articleNumber", "paragraphNumber", "articleTitle", "excerpt" }], "usedTool": bool, "retrievalMode": string }`
- [ ] The generation prompt instructs the model to answer **only** from retrieved chunks
      and to say it cannot answer if the regulations do not cover it
- [ ] Every factual claim in the answer maps to a returned citation
- [ ] Unanswerable questions return a refusal and an empty citations array — never a guess

### M3 — One agentic tool call

- [ ] A static `rosters.json` with 3–4 clubs: player name, position, whether club-trained,
      association-trained, or neither
- [ ] One tool registered with Microsoft Agent Framework: `GetSquad(clubName)`
- [ ] The agent calls it only when a question needs squad data alongside the rules
- [ ] Verified end-to-end: "Does Liverpool's squad meet the locally trained requirement?"
      retrieves 31.04, calls the tool, and reasons over both
- [ ] Pure-rules questions do **not** trigger the tool (`usedTool: false`)

Exactly one tool. Resist adding a second.

### M4 — Evaluation harness (the important part)

The gold set is supplied separately as `eval/questions.json` — 25 answerable questions with
paragraph-level gold references, and 5 that are unanswerable from this document.

- [ ] A runner that executes all 30 against the API and computes:
      - **Retrieval hit rate @k** — gold paragraph appears in the top-k retrieved chunks
      - **Citation accuracy** — the answer cites the gold paragraph
      - **Abstention rate** — of the 5 unanswerable, how many correctly refused
- [ ] Questions with multiple comma-separated gold paragraphs count as a hit only if all
      are retrieved
- [ ] Questions with `expectsToolCall: true` also assert `usedTool == true`
- [ ] Results written to `eval/results/<timestamp>.json`, tagged with retrieval mode and
      chunk settings, and printed as a table
- [ ] **The required experiment: run the full set three times — dense only, sparse only,
      hybrid — and commit all three results.** This is the headline result of the project.
- [ ] Optionally a second experiment varying one other thing (chunk size, or top-k)
- [ ] The three-way results table and a written interpretation are in the README

The written interpretation matters more than the numbers. Explain *why* each mode won or
lost on which questions, and what the failure cases had in common.

Four questions (`a19`, `a20`, `a21`, `a22`) are phrased to avoid the regulations' own
vocabulary and should favour dense retrieval. Questions naming exact figures should favour
sparse. If dense and sparse score identically across the set, something is wrong with one
of the two paths — check before reporting.

### M5 — Frontend

- [ ] Single page: question input, answer display, citations listed with paragraph numbers
- [ ] A visible indicator when the tool was called
- [ ] Loading and error states handled
- [ ] Ships as static files; no SSR, no routing library

Keep it plain and readable. This is not a design exercise.

### M6 — CI

- [ ] GitHub Actions workflow: build + unit tests on every push
- [ ] Postgres available as a service container so retrieval tests run in CI
- [ ] The eval harness runs on demand via `workflow_dispatch` (not every push — it costs tokens)
- [ ] README documents setup, required env vars, and how to run ingestion and eval

---

## 5. Repo structure

```
docker-compose.yml      Postgres + pgvector
/docs                   the downloaded regulations PDF
/db
  /migrations           schema and index DDL
/src
  /Ingestion            console app: parse, chunk, embed, insert
  /Api                  minimal API + retrieval + fusion + agent + tool
  /Api.Tests            unit tests: chunking, fusion, citation parsing, tool dispatch
/eval
  questions.json        the gold set (supplied — do not generate)
  /results              committed run outputs
/web                    React + Vite frontend
README.md
```

---

## 6. Testing expectations

Unit tests, not exhaustive coverage. Specifically:

- Chunker preserves paragraph numbers across page boundaries
- Chunker keeps lettered sub-items (`31.14(a)`, `31.14(c)`) distinguishable
- **RRF fusion: given two known ranked lists, the fused order matches a hand-computed
  expected result.** This is the one piece of real algorithmic logic in the project —
  test it properly, with a case where the two lists disagree
- Citation extraction handles an answer citing multiple paragraphs
- Tool dispatch fires for squad questions and stays silent for pure-rules questions

Tests that hit Azure OpenAI are out of scope — mock the embedding and completion clients.
Retrieval tests may use a real Postgres container with a small seeded fixture.

---

## 7. Working agreement

- Ask before adding any dependency not named in section 2
- Ask before adding a feature not in the milestone checklists
- Do not reach for a library that does hybrid search or RRF for you — writing it is the point
- Prefer the boring implementation; this code will be read aloud in an interview
- Keep functions small enough to explain in one sentence
- Commit at each milestone boundary with a message describing what now works
- If a milestone's checklist cannot be satisfied as written, stop and say so rather than
  silently reinterpreting the requirement

---

## 8. Done means

All six milestones pass their checklists, the README contains the three-way retrieval
comparison with written interpretation, and the whole thing runs from a clean clone with
`docker compose up` plus an Azure OpenAI endpoint.
