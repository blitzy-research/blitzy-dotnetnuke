/**
 * Wire contracts for the DotNetNuke page hierarchy - the abstraction that the database, the legacy source
 * and this contract all still call a "tab".
 */

import {
  decodeBoolean,
  decodeDateString,
  decodeInteger,
  decodeString,
  nullable,
  objectOf,
  type Decoder,
} from '../utils/decode.util';

export interface TabListItem {
  /**
   * The page's identifier. Zero is a legitimate value, because `dbo.Tabs.TabID` is `IDENTITY (0, 1)`: the
   * first page of an installation is numbered zero.
   */
  readonly tabId: number;

  /** The page name, captioned "Page Name" on the legacy screens. */
  readonly tabName: string;

  /** The browser-window title, captioned "Page Title", or `null` when none is stored. */
  readonly title: string | null;

  /**
   * The sort position of this page among its siblings under the same parent. Server-owned: the ordering
   * routine computes it, and the update shape deliberately omits it.
   */
  readonly tabOrder: number;

  /** The page this one hangs beneath, or `null` when it sits at the root of the hierarchy. */
  readonly parentId: number | null;

  /** The page's depth in the hierarchy, where zero denotes root level. */
  readonly level: number;

  /** The materialised hierarchical path of this page, or `null` when none is stored. */
  readonly tabPath: string | null;

  readonly isVisible: boolean;

  /** Whether the page is disabled, captioned "Disabled". */
  readonly disableLink: boolean;

  readonly isDeleted: boolean;

  /**
   * Whether any other page names this one as its parent. A computed existence projection rather than a
   * stored column, carried so that a node can decide whether to draw an expander without a further
   * request per row.
   */
  readonly hasChildren: boolean;

  /**
   * Whether the page must be served over a secure connection, captioned "Secure?". The newest column on
   * the page table: installations upgraded from an earlier schema acquired it with a default rather than
   * with a per-page decision.
   */
  readonly isSecure: boolean;

  /**
   * The navigation target used when the page acts as a link to another resource, captioned "Link Url", or
   * `null` when the page hosts content of its own.
   */
  readonly url: string | null;

  /**
   * The menu icon reference, captioned "Icon", or `null` for none. this may arrive as an unresolved
   * `fileid=NNN` token rather than as a usable path.
   */
  readonly iconFile: string | null;
}

/**
 * One page in full, returned by `GET /api/v1/tabs/{tabId}` and echoed back by `PUT /api/v1/tabs/{tabId}`.
 * Mirrors `Dtos/Tab/TabDetailDto.cs`.
 */
export interface TabDetail {
  /** The page's identifier. Zero is a legitimate value, because `dbo.Tabs.TabID` is `IDENTITY (0, 1)`. */
  readonly tabId: number;

  /**
   * The sort position of this page among its siblings under the same parent. Server-owned, and therefore
   * absent from the update shape: the legacy update path accepted no order argument at all, and the
   * ordering routine was deliberately invoked with zero so that it would recalculate the value itself.
   */
  readonly tabOrder: number;

  /**
   * The owning tenant, or `null` when this is a host-level page. the legacy property was a non-nullable
   * integer that used `-1` to mean "host page".
   */
  readonly portalId: number | null;

  /** The page name, captioned "Page Name" on the legacy screens. */
  readonly tabName: string;

  /** Whether the page appears in the navigation menu, captioned "Include In Menu". */
  readonly isVisible: boolean;

  /** The page this one hangs beneath, or `null` when it sits at the root of the hierarchy. */
  readonly parentId: number | null;

  /** The page's depth in the hierarchy, where zero denotes root level. */
  readonly level: number;

  /**
   * The menu icon reference, captioned "Icon", or `null` for none. may arrive as an unresolved
   * `fileid=NNN` token rather than as a usable path; see the list row's note.
   */
  readonly iconFile: string | null;

  /**
   * Whether the page is disabled, captioned "Disabled". a known legacy behaviour is preserved rather than
   * corrected here.
   */
  readonly disableLink: boolean;

  /** The browser-window title, captioned "Page Title", or `null` when none is stored. */
  readonly title: string | null;

  /** The page description, or `null` when none is stored. */
  readonly description: string | null;

  /**
   * The comma-separated search keywords, captioned "Keywords", or `null` when none are stored. the column
   * and the legacy property are both spelled with an interior capital; the backend renames the member to
   * this single-word form and maps it back to the column in the persistence layer, so the wire name is
   * `keywords`.
   */
  readonly keywords: string | null;

  /** Whether the page is soft-deleted into the recycle bin. */
  readonly isDeleted: boolean;

  /**
   * The navigation target used when the page acts as a link to another resource, captioned "Link Url", or
   * `null` when the page hosts content of its own.
   */
  readonly url: string | null;

  /**
   * The opaque skin token applied to this page, captioned "Page Skin", or `null` for none. this is a
   * genuinely stored column that the terminal update procedure writes, so it belongs on both the read and
   * the write surface and is carried here rather than dropped.
   */
  readonly skinSrc: string | null;

  /**
   * The opaque container token applied to the modules on this page, captioned "Page Container", or `null`
   * for none. as with the skin token, this is a genuinely stored column that the terminal update
   * procedure writes, so it is carried on both surfaces; and as with the skin token it is INERT here,
   * stored and returned verbatim with no resolution, no file lookup and no rendering anywhere in this
   * target.
   */
  readonly containerSrc: string | null;

  /** The materialised hierarchical path of this page, or `null` when none is stored. */
  readonly tabPath: string | null;

  /**
   * The instant from which the page becomes available as an ISO 8601 string, or `null` when no start date
   * is set. Captioned "Start Date:" on the legacy screens.
   */
  readonly startDate: string | null;

  /**
   * The instant after which the page ceases to be available as an ISO 8601 string, or `null` when no end
   * date is set. Captioned "End Date:" on the legacy screens.
   */
  readonly endDate: string | null;

  /**
   * The automatic refresh interval in SECONDS, or `null` when the page does not refresh automatically.
   * Captioned "Refresh Interval (seconds)" on the legacy screens.
   */
  readonly refreshInterval: number | null;

  /**
   * Raw markup injected into the document head when the page is rendered, captioned "Page Header Tags",
   * or `null` when none is stored. this is arbitrary author-supplied markup, carried verbatim.
   */
  readonly pageHeadText: string | null;

  /** Whether the page must be served over a secure connection, captioned "Secure?". */
  readonly isSecure: boolean;

  /**
   * Whether any other page names this one as its parent. A computed existence projection rather than a
   * stored column.
   */
  readonly hasChildren: boolean;
}

/** The body of `PUT /api/v1/tabs/{tabId}`. Mirrors `Dtos/Tab/UpdateTabRequest.cs`. */
export interface UpdateTabRequest {
  /** The page name, captioned "Page Name" on the legacy screens. */
  readonly tabName: string;

  /** The browser-window title, captioned "Page Title", or `null` to clear it. */
  readonly title: string | null;

  /** The page description, or `null` to clear it. */
  readonly description: string | null;

  /**
   * The comma-separated search keywords, captioned "Keywords", or `null` to clear them. At most 500
   * characters.
   */
  readonly keywords: string | null;

  /**
   * The page this one should hang beneath, or `null` to make it a root-level page. `null` is an
   * instruction to MOVE THE PAGE TO THE ROOT. It does not mean "leave the parent as it is" - this request
   * replaces the editable subset wholesale.
   */
  readonly parentId: number | null;

  /** Whether the page should appear in the navigation menu, captioned "Include In Menu". */
  readonly isVisible: boolean;

  /** Whether the page should be disabled, captioned "Disabled". */
  readonly disableLink: boolean;

  /** The menu icon reference, captioned "Icon", or `null` to clear it. */
  readonly iconFile: string | null;

  /**
   * The navigation target when the page should act as a link to another resource, captioned "Link Url",
   * or `null` to clear it. At most 255 characters, and it must occupy a single line: the backend refuses
   * any Unicode control character, on the ground that no single-line form field could ever have submitted
   * one even though the column could store it.
   */
  readonly url: string | null;

  /**
   * The instant from which the page becomes available as an ISO 8601 string, or `null` for no start
   * bound. Captioned "Start Date:" on the legacy screens.
   */
  readonly startDate: string | null;

  /**
   * The instant after which the page ceases to be available as an ISO 8601 string, or `null` for no end
   * bound. Captioned "End Date:" on the legacy screens.
   */
  readonly endDate: string | null;

  /**
   * The automatic refresh interval in SECONDS, or `null` for no automatic refresh. send `null` rather
   * than `-1`, which was the legacy sentinel.
   */
  readonly refreshInterval: number | null;

  /**
   * Raw markup to inject into the document head, captioned "Page Header Tags", or `null` to clear it.
   * Stored verbatim and never sanitised by the server.
   */
  readonly pageHeadText: string | null;

  /** Whether the page must be served over a secure connection, captioned "Secure?". */
  readonly isSecure: boolean;

  /**
   * Whether the page should sit in the recycle bin: `true` soft-deletes it, `false` restores it.
   * MIGRATION: this is the only member of this shape with no counterpart control on the legacy
   * page-settings form, and it is present for a measured reason.
   */
  readonly isDeleted: boolean;
}

/**
 * Decodes one listed page. `tabId` uses {@link decodeInteger} with no positivity test: `dbo.Tabs` is
 * declared `[TabID] [int] IDENTITY (0, 1)`, so ZERO is the first page an installation ever creates.
 */
export const decodeTabListItem: Decoder<TabListItem> = objectOf<TabListItem>({
  tabId: decodeInteger,
  tabName: decodeString,
  title: nullable(decodeString),
  tabOrder: decodeInteger,
  parentId: nullable(decodeInteger),
  level: decodeInteger,
  tabPath: nullable(decodeString),
  isVisible: decodeBoolean,
  disableLink: decodeBoolean,
  isDeleted: decodeBoolean,
  hasChildren: decodeBoolean,
  isSecure: decodeBoolean,
  url: nullable(decodeString),
  iconFile: nullable(decodeString),
});

/**
 * Decodes one page in full. `portalId` is nullable AND admits minus one, which is not a contradiction:
 * `dbo.Portals` is declared `[PortalID] [int] IDENTITY (-1, 1)`, so minus one is the FIRST portal and a
 * real owner, while null means the page names no portal.
 */
export const decodeTabDetail: Decoder<TabDetail> = objectOf<TabDetail>({
  tabId: decodeInteger,
  tabOrder: decodeInteger,
  portalId: nullable(decodeInteger),
  tabName: decodeString,
  isVisible: decodeBoolean,
  parentId: nullable(decodeInteger),
  level: decodeInteger,
  iconFile: nullable(decodeString),
  disableLink: decodeBoolean,
  title: nullable(decodeString),
  description: nullable(decodeString),
  keywords: nullable(decodeString),
  isDeleted: decodeBoolean,
  url: nullable(decodeString),
  skinSrc: nullable(decodeString),
  containerSrc: nullable(decodeString),
  tabPath: nullable(decodeString),
  startDate: nullable(decodeDateString),
  endDate: nullable(decodeDateString),
  refreshInterval: nullable(decodeInteger),
  pageHeadText: nullable(decodeString),
  isSecure: decodeBoolean,
  hasChildren: decodeBoolean,
});
