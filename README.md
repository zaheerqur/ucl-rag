# UCL Regulations RAG

A retrieval-augmented generation system over the UEFA Champions League 2025/26 competition regulations. Answers natural-language questions with citations to specific paragraphs, correctly declines out-of-scope questions, and calls a roster tool when a question requires both rule text and squad data.

Built in .NET 10, Postgres 16 with pgvector, and Azure OpenAI.

---

## Stack

| Layer | Choice |
|---|---|
| API | ASP.NET Core minimal API |
| Store | Postgres 16 + pgvector (Docker) |
| Dense retrieval | pgvector cosine similarity |
| Sparse retrieval | Postgres `tsvector` / `ts_rank` |
| Fusion | Hand-written Reciprocal Rank Fusion |
| Embeddings + generation | Azure OpenAI (`text-embedding-3-small`, `gpt-4o`) |
| Agent / tool calling | Microsoft Agent Framework (`Microsoft.Agents.AI.OpenAI`) |
| Frontend | React 19 + TypeScript + Vite |
| CI | GitHub Actions |

---

## Setup

### Prerequisites

- Docker
- .NET 10 SDK
- Node.js 20+
- An Azure OpenAI resource with `text-embedding-3-small` and `gpt-4o` deployments

### 1. Start Postgres

```bash
docker compose up -d
```

### 2. Configure secrets

```bash
cd src/Api
dotnet user-secrets set "AzureOpenAI:ApiKey" "<your-key>"
```

The endpoint, deployment names, and connection string are in `src/Api/appsettings.json`.

### 3. Ingest the regulations

Place the PDF at `docs/UCL_Regulations_2025-26.pdf`, then:

```bash
dotnet run --project src/Ingestion
```

Ingestion is idempotent — re-running it will not create duplicate rows.

### 4. Run the API

```bash
dotnet run --project src/Api
# Listening on http://localhost:5001
```

Switch retrieval mode at runtime without rebuild:

```bash
Retrieval__Mode=sparse dotnet run --project src/Api
# or: dense | sparse | hybrid (default)
```

### 5. Run the frontend

```bash
cd web
npm install
npm run dev
# http://localhost:5173
```

---

## API

```
POST /ask
{ "question": "How many locally trained players must a club register?" }
```

```json
{
  "answer": "A club must include a minimum of eight locally trained players on List A (paragraph 31.04).",
  "citations": [
    {
      "articleNumber": "31",
      "paragraphNumber": "04",
      "articleTitle": "Locally Trained Players",
      "excerpt": "No club may have more than 25 players on List A..."
    }
  ],
  "usedTool": false,
  "retrievalMode": "hybrid",
  "retrievedParagraphRefs": ["31.04", "31.08", "31.10", ...]
}
```

---

## Evaluation

### Running the harness

```bash
# API must be running
dotnet run --project src/Eval
```

Override settings without rebuilding:

```bash
dotnet run --project src/Eval -- --Eval:QuestionsPath=eval/questions.json --Eval:ResultsDir=eval/results
```

Results are written to `eval/results/<timestamp>_<mode>.json`.

### Three-mode experiment

The harness was run three times — dense only, sparse only, hybrid — over 30 questions (25 answerable, 5 unanswerable). Results are in `eval/results/`.

| Metric | Dense | Sparse | Hybrid |
|---|---|---|---|
| Retrieval hit rate @10 | **84.0%** | 52.0% | 80.0% |
| Citation accuracy | **56.0%** | 12.0% | 40.0% |
| Abstention rate | 100.0% | 100.0% | 100.0% |
| Tool-call accuracy | 100.0% | 100.0% | 100.0% |

*25 answerable questions, 5 unanswerable, 3 with `expectsToolCall: true`. TopK=10, RrfK=60.*

### Interpretation

**Dense wins on both metrics.** Semantic embeddings bridge the vocabulary gap between question phrasing and regulation text naturally. A question asking about "finalists" retrieves the paragraph that says "runner-up"; a question about a "squad list" retrieves the paragraph that says "List A". The model then reads the chunk and either cites it or does not.

**Sparse reached 52% hit rate** once a correctness bug was fixed. The initial implementation used `plainto_tsquery`, which produces a strict AND of every content word in the question. Because any one missing term eliminates a chunk from the result set entirely, even questions with strong lexical overlap returned zero results — "What share of stadium capacity has to be made available to visiting supporters?" generates the tsquery `'share' & 'stadium' & 'capac' & 'made' & 'avail' & 'visit' & 'support'`, and no single chunk contains all seven stems. The fix converts `&` to `|` before casting back to `tsquery`, so `ts_rank` scores every chunk that matches any query term. This is true BM25 behaviour: all chunks are scored, and chunks matching more terms rank higher. After the fix, the 13 questions that sparse hits are mostly those where the question and the regulation share a concrete noun: "medals", "goalkeepers", "substitutions".

The 12 sparse misses split into two failure types. Six are vocabulary-gap questions where the question word simply does not appear in the chunk — "locally trained players" is five words, four of which stem correctly, but the question says "squad" and the regulation says "List A", so the `squad` stem contributes nothing. Six are specificity failures: the correct chunk is retrieved but falls outside the top-10 because other chunks score higher on the OR query's common terms.

**Hybrid is worse than dense alone** on both hit rate (80% vs 84%) and citation accuracy (40% vs 56%). This is the expected failure mode of RRF when one component is noisy: the sparse OR-list promotes chunks that match common question words ("club", "player") regardless of relevance, and RRF gives those chunks partial credit from the sparse rank. For one question (a17, common ownership), the correct dense-rank-1 result was displaced from the top-10 by sparse noise. Citation accuracy drops further because RRF reorders the list relative to the pure dense ranking, and the model tends to cite the top-ranked chunk; if the gold chunk moves from rank 1 to rank 4, the model may cite something else even though the chunk is still present.

**Four questions miss across all three modes** (a14, a15, a18, a21). These are questions where the gold paragraph sits beyond position 10 in all three rankings. They share a pattern: the question is a specific procedural edge case ("A match is abandoned at half-time and resumed the following day — which rules apply?") whose answer lives in a short clause embedded in a long administrative article, and the article contains many other topics that match common question terms more strongly. Increasing TopK from 10 to 20 or reranking with a cross-encoder are the obvious next experiments.

**Abstention and tool-call accuracy are perfect across all three modes.** The system prompt instructs the model to return an empty citations array when the regulations do not cover the question, and all five out-of-scope questions triggered correct refusals regardless of what the retriever returned. All three squad compliance questions triggered the `GetSquad` tool call and none of the pure-rules questions did, which confirms the agent's routing logic is working correctly.

---

## Tests

```bash
dotnet test src/Api.Tests
```

Unit tests cover: chunker paragraph preservation across page boundaries, lettered sub-items, oversized-chunk splitting, RRF fusion order against a hand-computed expected result, and zero null paragraph numbers.

---

## Repo structure

```
docker-compose.yml        Postgres + pgvector
docs/                     downloaded regulations PDF (not committed)
db/migrations/            schema DDL
src/
  Ingestion/              parse, chunk, embed, insert
  Api/                    minimal API, retrieval, fusion, agent, tool
  Api.Tests/              unit tests
  Eval/                   evaluation runner
eval/
  questions.json          gold set (25 answerable + 5 unanswerable)
  results/                committed run outputs
web/                      React + Vite frontend
data/
  rosters.json            static squad data for the tool call
```
