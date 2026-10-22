export interface KeyValueStore {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
  delete(key: string): Promise<void>;
  list(prefix: string): Promise<string[]>;
}

export class MemoryStore implements KeyValueStore {
  readonly values = new Map<string, string>();
  async get(key: string): Promise<string | null> { return this.values.get(key) ?? null; }
  async set(key: string, value: string): Promise<void> { this.values.set(key, value); }
  async delete(key: string): Promise<void> { this.values.delete(key); }
  async list(prefix: string): Promise<string[]> {
    return [...this.values.keys()].filter((key) => key.startsWith(prefix)).sort();
  }
}

interface BlobStoreLike {
  get(key: string, options: { type: "text" }): Promise<string | null>;
  set(key: string, value: string): Promise<unknown>;
  delete(key: string): Promise<void>;
  list(options: { prefix: string; paginate: true }): AsyncIterable<{ blobs: { key: string }[] }>;
}

export function blobStore(store: BlobStoreLike): KeyValueStore {
  return {
    get: (key) => store.get(key, { type: "text" }),
    set: async (key, value) => { await store.set(key, value); },
    delete: (key) => store.delete(key),
    async list(prefix) {
      const keys: string[] = [];
      for await (const page of store.list({ prefix, paginate: true })) keys.push(...page.blobs.map((blob) => blob.key));
      return keys.sort();
    },
  };
}

export async function metricsStore(): Promise<KeyValueStore> {
  const { getStore } = await import("@netlify/blobs");
  return blobStore(getStore({ name: "annotator-metrics", consistency: "strong" }));
}
