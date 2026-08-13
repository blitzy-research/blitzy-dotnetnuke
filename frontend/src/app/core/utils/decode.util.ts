/**
 * Runtime decoding of external JSON at the API boundary. WHY THIS FILE EXISTS Every response type in
 * `core/models/` is a COMPILE-TIME declaration.
 */

import type { ApiMeta, ApiResponse, PagedResult } from '../models/paged-result.model';

/**
 * A total function from an untrusted value to a declared type.
 *
 * @param value The untrusted value, as parsed from the response body.
 * @param path A dotted/indexed trail naming the value's position in the document, used only to describe a
 * violation.
 * @returns The decoded value.
 * @throws ContractViolationError When the value does not satisfy the contract.
 */
export type Decoder<T> = (value: unknown, path: string) => T;

/** One decoder per declared member of `T`, with optionality removed. `-?` is the load-bearing part. */
export type DecoderShape<T> = { readonly [K in keyof T]-?: Decoder<T[K]> };

/**
 * Raised when an external document does not satisfy the contract this client declares for it.
 * Distinguishable by type so that a caller can tell "the server answered something this client cannot
 * read" apart from "the server refused the request", which is an `HttpErrorResponse`.
 */
export class ContractViolationError extends Error {
  /** The dotted/indexed position of the offending value within the document. */
  readonly path: string;

  /** A description of what the contract requires at {@link path}. */
  readonly expected: string;

  /** The TYPE of what arrived — never the value, which may carry credentials. */
  readonly received: string;

  /**
   * @param path The offending value's position, for example `response.data.items[2]`.
   * @param expected A short description of the requirement, for example `a string`.
   * @param value The offending value, used only to derive its type name.
   */
  constructor(path: string, expected: string, value: unknown) {
    const received = describeType(value);

    super(
      `The server response did not match the expected contract at ${path}: ` +
        `expected ${expected}, received ${received}.`,
    );

    this.name = 'ContractViolationError';
    this.path = path;
    this.expected = expected;
    this.received = received;
  }
}

/**
 * Whether a thrown value is a contract violation.
 *
 * @param error A caught value.
 * @returns True when the value is a contract violation.
 */
export function isContractViolation(error: unknown): error is ContractViolationError {
  return (
    error instanceof ContractViolationError ||
    (error instanceof Error && error.name === 'ContractViolationError')
  );
}

/**
 * Names the type of an arbitrary value without disclosing it.
 *
 * @param value Any value.
 * @returns A short type name such as `null`, `an array`, `a string`.
 */
function describeType(value: unknown): string {
  if (value === null) {
    return 'null';
  }

  if (Array.isArray(value)) {
    return 'an array';
  }

  switch (typeof value) {
    case 'undefined':
      return 'nothing';
    case 'string':
      return 'a string';
    case 'number':
      return 'a number';
    case 'boolean':
      return 'a boolean';
    case 'object':
      return 'an object';
    default:
      return `a ${typeof value}`;
  }
}

/**
 * Decodes a string. The empty string is ADMITTED, deliberately.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The string.
 */
export const decodeString: Decoder<string> = (value, path) => {
  if (typeof value !== 'string') {
    throw new ContractViolationError(path, 'a string', value);
  }

  return value;
};

/**
 * Decodes a string that must carry at least one non-whitespace character.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The string, exactly as received — leading and trailing whitespace is preserved, because
 * trimming here would rewrite a wire value rather than validate it.
 */
export const nonEmptyString: Decoder<string> = (value, path) => {
  const text = decodeString(value, path);

  if (text.trim().length === 0) {
    throw new ContractViolationError(path, 'a non-blank string', value);
  }

  return text;
};

/**
 * Decodes a finite number.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The number.
 */
export const decodeNumber: Decoder<number> = (value, path) => {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new ContractViolationError(path, 'a finite number', value);
  }

  return value;
};

/**
 * Decodes an integer. ⚠ SENTINEL SAFETY. `0` and `-1` are LEGITIMATE identifiers in this schema and must
 * pass. `Portals.PortalID` is `IDENTITY(-1,1)`, so the first portal is `0` and `-1` is a real row
 * identifier as well as the legacy absent-marker; `Roles.RoleID`, `Tabs.TabID` and `Modules.ModuleID` are
 * `IDENTITY(0,1)`.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The integer.
 */
export const decodeInteger: Decoder<number> = (value, path) => {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new ContractViolationError(path, 'an integer', value);
  }

  return value;
};

/**
 * Decodes a boolean.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The boolean.
 */
export const decodeBoolean: Decoder<boolean> = (value, path) => {
  if (typeof value !== 'boolean') {
    throw new ContractViolationError(path, 'a boolean', value);
  }

  return value;
};

/**
 * Decodes a plain object, excluding `null` and arrays. Both exclusions matter: `typeof null === 'object'`
 * and `typeof [] === 'object'`, so without them a null member or a collection arriving where an object
 * was declared would pass and fault on first member read.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The value as an indexable record.
 */
export const decodeObject: Decoder<Record<string, unknown>> = (value, path) => {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new ContractViolationError(path, 'an object', value);
  }

  return value as Record<string, unknown>;
};

/**
 * Decodes an ISO-8601 date-time string.
 *
 * @param value The untrusted value.
 * @param path The value's position.
 * @returns The date string, unchanged.
 */
export const decodeDateString: Decoder<string> = (value, path) => {
  const text = decodeString(value, path);

  if (Number.isNaN(Date.parse(text))) {
    throw new ContractViolationError(path, 'an ISO-8601 date-time string', value);
  }

  return text;
};

/**
 * Admits `null` in addition to whatever the inner decoder admits.
 *
 * @param inner The decoder for the non-null case.
 * @returns A decoder admitting the inner type or `null`.
 */
export function nullable<T>(inner: Decoder<T>): Decoder<T | null> {
  return (value, path) => (value === null ? null : inner(value, path));
}

/**
 * Admits an absent member in addition to whatever the inner decoder admits.
 *
 * @param inner The decoder for the present case.
 * @returns A decoder admitting the inner type or `undefined`.
 */
export function optional<T>(inner: Decoder<T>): Decoder<T | undefined> {
  return (value, path) => (value === undefined ? undefined : inner(value, path));
}

/**
 * Decodes an array, applying `item` to every element. The element path carries its index, so a violation
 * in the fortieth record of a page names that record rather than the page.
 *
 * @param item The element decoder.
 * @returns A decoder producing a read-only array.
 */
export function arrayOf<T>(item: Decoder<T>): Decoder<readonly T[]> {
  return (value, path) => {
    if (!Array.isArray(value)) {
      throw new ContractViolationError(path, 'an array', value);
    }

    // A plain loop rather than `map`, so the index is available for the path and no
    // intermediate closure is allocated per element on a page of records.
    const decoded: T[] = [];

    for (let index = 0; index < value.length; index += 1) {
      decoded.push(item(value[index], `${path}[${index}]`));
    }

    return decoded;
  };
}

/**
 * Decodes a string-keyed dictionary, applying `entry` to every value. This is the shape the settings
 * endpoints use — module and tab-module settings are genuine key/value rows in the legacy schema, so
 * their wire form is an open dictionary rather than a declared object, and no shape can be checked for
 * it.
 *
 * @param entry The value decoder.
 * @returns A decoder producing a read-only record.
 */
export function recordOf<T>(entry: Decoder<T>): Decoder<Readonly<Record<string, T>>> {
  return (value, path) => {
    const source = decodeObject(value, path);
    const decoded: Record<string, T> = Object.create(null) as Record<string, T>;

    for (const key of Object.keys(source)) {
      decoded[key] = entry(source[key], `${path}.${key}`);
    }

    // Re-spread onto a normal object literal so consumers see an ordinary object with
    // the expected prototype, while the assembly above stayed prototype-free.
    return { ...decoded };
  };
}

/**
 * Decodes a value that must be one of a closed set of string codes.
 *
 * @param allowed The permitted codes.
 * @returns A decoder producing one of the permitted codes.
 */
export function oneOf<T extends string>(allowed: readonly T[]): Decoder<T> {
  const expected = `one of ${allowed.map((code) => `'${code}'`).join(', ')}`;

  return (value, path) => {
    const text = decodeString(value, path);

    // A widened comparison, because `includes` on a `readonly T[]` will not accept a
    // plain string without it.
    if (!(allowed as readonly string[]).includes(text)) {
      throw new ContractViolationError(path, expected, value);
    }

    return text as T;
  };
}

/**
 * Decodes a value that must be one of a closed set of numeric codes. The numeric counterpart of {@link
 * oneOf}, for the integer discriminator columns the legacy schema stores rather than lookup rows: the
 * portal's registration mode and banner advertising mode, and a module's visibility.
 *
 * @param allowed The permitted codes.
 * @returns A decoder producing one of the permitted codes.
 */
export function oneOfNumber<T extends number>(allowed: readonly T[]): Decoder<T> {
  const expected = `one of ${allowed.join(', ')}`;

  return (value, path) => {
    const code = decodeInteger(value, path);

    if (!(allowed as readonly number[]).includes(code)) {
      throw new ContractViolationError(path, expected, value);
    }

    return code as T;
  };
}

/**
 * Decodes an object against one decoder per declared member.
 *
 * @param shape One decoder per member of `T`.
 * @returns A decoder producing `T`.
 */
export function objectOf<T extends object>(shape: DecoderShape<T>): Decoder<T> {
  // Enumerated once rather than per call: the shape is a module-level constant at every
  // call site, and a page of records would otherwise re-enumerate it per element.
  const members: readonly string[] = Object.keys(shape);

  const decoders = shape as Readonly<Record<string, Decoder<unknown>>>;

  return (value, path) => {
    const source = decodeObject(value, path);
    const decoded: Record<string, unknown> = {};

    for (const member of members) {
      decoded[member] = decoders[member](source[member], `${path}.${member}`);
    }

    // Every declared member has been assigned by the loop above: `members` is the full key set of the
    // shape, and {@link DecoderShape} makes the shape's key set the full key set of `T` with optionality
    // removed.
    return decoded as T;
  };
}

/**
 * The decoder for {@link ApiMeta}. The three coordinates a pager cannot be rendered without — the total,
 * the zero-based page index and the page size the server applied — are REQUIRED and must be integers.
 */
export const decodeApiMeta: Decoder<ApiMeta> = (value, path) => {
  const meta = decodeObject(value, path);

  const totalCount = decodeInteger(meta['totalCount'], `${path}.totalCount`);
  const pageIndex = decodeInteger(meta['pageIndex'], `${path}.pageIndex`);
  const pageSize = decodeInteger(meta['pageSize'], `${path}.pageSize`);

  const declaredTotalPages: unknown = meta['totalPages'];
  const totalPages =
    declaredTotalPages === undefined || declaredTotalPages === null
      ? derivePageCount(totalCount, pageSize)
      : decodeInteger(declaredTotalPages, `${path}.totalPages`);

  return { totalCount, pageIndex, pageSize, totalPages };
};

/**
 * Derives the page count from the total and the page size.
 *
 * @param totalCount The total across every page.
 * @param pageSize The page size the server applied.
 * @returns The number of pages.
 */
function derivePageCount(totalCount: number, pageSize: number): number {
  if (pageSize <= 0 || totalCount <= 0) {
    return 0;
  }

  return Math.floor(totalCount / pageSize) + (totalCount % pageSize > 0 ? 1 : 0);
}

/**
 * Validates the framing of a page without inspecting its records. The single implementation of "is this a
 * page?", shared by {@link pageOf} here and by `toPagedResult` in `paged-result.model.ts`.
 *
 * @param value The untrusted response body.
 * @param path The body's position.
 * @returns The undecoded records and the validated paging coordinates.
 * @throws ContractViolationError When the body is not a page.
 */
export function decodePageStructure(
  value: unknown,
  path: string,
): { readonly items: readonly unknown[]; readonly meta: ApiMeta } {
  const page = decodeObject(value, path);
  const items: unknown = page['items'];

  if (!Array.isArray(items)) {
    throw new ContractViolationError(`${path}.items`, 'an array', items);
  }

  return {
    items: items as readonly unknown[],
    meta: decodeApiMeta(page['meta'], `${path}.meta`),
  };
}

/**
 * Decodes the single-payload success envelope.
 *
 * @param data The decoder for the payload.
 * @returns A decoder producing the payload, with the envelope already validated and stripped — callers
 * want the payload, and returning the envelope would make every call site unwrap it again.
 */
export function envelopeOf<T>(data: Decoder<T>): Decoder<T> {
  return (value, path) => {
    const envelope = decodeObject(value, path);

    // An ABSENT `meta` is tolerated, and the asymmetry with the page envelope is deliberate.
    optional(nullable(decodeApiMeta))(envelope['meta'], `${path}.meta`);

    return data(envelope['data'], `${path}.data`);
  };
}

/**
 * Decodes a single-payload envelope and returns it WHOLE rather than stripped.
 *
 * @param data The decoder for the payload.
 * @returns A decoder producing the whole envelope.
 */
export function responseOf<T>(data: Decoder<T>): Decoder<ApiResponse<T>> {
  return (value, path) => {
    const envelope = decodeObject(value, path);
    const meta = optional(nullable(decodeApiMeta))(envelope['meta'], `${path}.meta`);

    return {
      data: data(envelope['data'], `${path}.data`),
      meta: meta ?? null,
    };
  };
}

/**
 * Decodes the envelope of a response whose payload is a page of records. Kept separate from {@link
 * envelopeOf} because the two envelopes differ in exactly one respect that matters — a page's metadata is
 * required and populated, where a single payload's is null — and folding them together is what allowed a
 * page with no metadata to be read as a successful empty first page.
 *
 * @param item The record decoder.
 * @returns A decoder producing a fully validated page.
 */
export function pageOf<T>(item: Decoder<T>): Decoder<PagedResult<T>> {
  return (value, path) => {
    const page = decodePageStructure(value, path);
    const decoded: T[] = [];

    for (let index = 0; index < page.items.length; index += 1) {
      decoded.push(item(page.items[index], `${path}.items[${index}]`));
    }

    return { items: decoded, meta: page.meta };
  };
}

/** Convenience for decoding a whole response body from a service method. */
export const RESPONSE_ROOT = 'response';

/**
 * Applies a decoder to a response body using the conventional root path.
 *
 * @param decoder The decoder to apply.
 * @param body The untrusted response body.
 * @returns The decoded value.
 */
export function decodeResponse<T>(decoder: Decoder<T>, body: unknown): T {
  return decoder(body, RESPONSE_ROOT);
}
