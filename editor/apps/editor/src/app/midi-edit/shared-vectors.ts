/**
 * Reads one of the Model's shared test vectors (engine/tests/Anode.Model.Tests/<name>) for a parity spec: parsed JSON,
 * or null when the file is not there (workstream E writes them; the spec then skips) or there is no file system (a
 * browser test run). For specs only.
 */
export async function loadVectors(name: string): Promise<unknown> {
  const proc = (globalThis as { process?: { cwd(): string; env: Record<string, string | undefined> } }).process;
  if (!proc) {
    return null;
  }
  try {
    const fsName = 'node:fs';
    const fs = (await import(/* @vite-ignore */ fsName)) as {
      existsSync(path: string): boolean;
      readFileSync(path: string, encoding: string): string;
    };
    const root = proc.cwd().replace(/\/editor(\/.*)?$/, '');
    const path = `${root}/engine/tests/Anode.Model.Tests/${name}`;
    return fs.existsSync(path) ? (JSON.parse(fs.readFileSync(path, 'utf8')) as unknown) : null;
  } catch {
    return null;
  }
}

/** A vector file's cases: the file itself when it is an array, else its `cases`. */
export function vectorCases<T>(loaded: unknown): T[] {
  if (Array.isArray(loaded)) {
    return loaded as T[];
  }
  return (loaded as { cases?: T[] } | null)?.cases ?? [];
}
