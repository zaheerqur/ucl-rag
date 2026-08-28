CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS chunks (
    id              SERIAL PRIMARY KEY,
    article_number  TEXT    NOT NULL,
    paragraph_number TEXT   NOT NULL,
    article_title   TEXT    NOT NULL,
    chunk_text      TEXT    NOT NULL,
    embedding       vector(1536),
    text_search     tsvector GENERATED ALWAYS AS (to_tsvector('english', chunk_text)) STORED,
    UNIQUE (article_number, paragraph_number)
);

CREATE INDEX IF NOT EXISTS chunks_embedding_hnsw_idx
    ON chunks USING hnsw (embedding vector_cosine_ops);

CREATE INDEX IF NOT EXISTS chunks_text_search_gin_idx
    ON chunks USING gin (text_search);
