import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { map } from 'rxjs';

import { API_ENDPOINTS } from '../config/api-endpoints';
import {
  decodeModuleDefinition,
  decodeModuleDetail,
  decodeModuleListItem,
  decodeModuleSettingsBag,
} from '../models/module.model';
import {
  arrayOf,
  decodeResponse,
  decodeString,
  envelopeOf,
  pageOf,
} from '../utils/decode.util';
import { moduleListParams, modulePlacementParams } from '../utils/http-params.util';
import { presentedInContext } from './notification.service';

import type { HttpParams } from '@angular/common/http';
import type { Observable } from 'rxjs';

import type {
  ModuleListFilter,
  ModulePlacementSelector,
  PagedRequestParams,
} from '../utils/http-params.util';
import type {
  CreateModuleRequest,
  ModuleDefinition,
  ModuleDetail,
  ModuleExportRequest,
  ModuleImportRequest,
  ModuleListPage,
  ModuleSettingsBag,
  UpdateModuleRequest,
} from '../models/module.model';
import type { Decoder } from '../utils/decode.util';

/**
 * One decoder per response shape this transport reads, composed once at module scope. ⚠ NOT ONE OF THEM
 * IS `nullable`, AND THAT MIRRORS THE CONTRACT RATHER THAN BEING OPTIMISTIC. Every one of these endpoints
 * answers a successful read with a populated payload or refuses the read outright; "the target does not
 * resolve" is a `404` problem document and never a `200` carrying nothing.
 */
const MODULE_PAGE: Decoder<ModuleListPage> = pageOf(decodeModuleListItem);
const MODULE_DETAIL_RESPONSE: Decoder<ModuleDetail> = envelopeOf(decodeModuleDetail);
const MODULE_SETTINGS_RESPONSE: Decoder<ModuleSettingsBag> = envelopeOf(decodeModuleSettingsBag);
const MODULE_DEFINITION_LIST_RESPONSE: Decoder<readonly ModuleDefinition[]> = envelopeOf(
  arrayOf(decodeModuleDefinition),
);
const MODULE_DEFINITION_RESPONSE: Decoder<ModuleDefinition> = envelopeOf(decodeModuleDefinition);

/**
 * Typed transport for the module placement and module definition endpoints. ONE METHOD, ONE ENDPOINT, ONE
 * REQUEST. The migration discipline restricts Angular services to API communication, and this class is
 * written to that rule literally: every member below issues exactly one request and hands back the
 * observable cold.
 */
@Injectable({ providedIn: 'root' })
export class ModuleService {
  private readonly http = inject(HttpClient);

  /**
   * Lists the resolved tenant's module placements, one page at a time. `GET /modules`, answering `200`
   * with a page envelope.
   *
   * @param request The page coordinate, page size, ordering and free-text filter.
   * @param filter The page restriction and whether soft-deleted rows are included, or omitted to list the
   * tenant's live placements.
   * @returns The requested page, with every paging coordinate present.
   */
  listModules(
    request: PagedRequestParams,
    filter?: ModuleListFilter | null,
  ): Observable<ModuleListPage> {
    const params: HttpParams = moduleListParams(request, filter);

    // The paged listing is the one endpoint here whose body IS the envelope, so no payload member is lifted
    // out of it.
    return this.http
      .get<unknown>(API_ENDPOINTS.modules.collection(), {
        params,
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MODULE_PAGE, body)));
  }

  /**
   * Reads one module placement in full. `GET /modules/{moduleId}`, answering `200` with the placement, or
   * `404` when no such module is visible to the caller.
   *
   * @param moduleId The module to read.
   * @param placement The placement to address, or omitted to address the module.
   * @returns The placement.
   */
  getModule(
    moduleId: number,
    placement?: ModulePlacementSelector | null,
  ): Observable<ModuleDetail> {
    const params: HttpParams = modulePlacementParams(placement);

    return this.http
      .get<unknown>(API_ENDPOINTS.modules.byId(moduleId), {
        params,
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MODULE_DETAIL_RESPONSE, body)));
  }

  /**
   * Adds a module placement to the resolved tenant. `POST /modules`, answering `201` with the created
   * placement.
   *
   * @param request The placement to create, transmitted whole.
   * @returns The created placement.
   */
  createModule(request: CreateModuleRequest): Observable<ModuleDetail> {
    return this.http
      .post<unknown>(API_ENDPOINTS.modules.collection(), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MODULE_DETAIL_RESPONSE, body)));
  }

  /**
   * Replaces one module placement.
   *
   * @param moduleId The module to replace.
   * @param request The complete replacement state, transmitted whole.
   * @returns The updated placement.
   */
  updateModule(moduleId: number, request: UpdateModuleRequest): Observable<ModuleDetail> {
    return this.http
      .put<unknown>(API_ENDPOINTS.modules.byId(moduleId), request, {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MODULE_DETAIL_RESPONSE, body)));
  }

  /**
   * Removes one module placement. `DELETE /modules/{moduleId}`, answering `204` with no body.
   *
   * @param moduleId The module to remove.
   * @param placement The single placement to remove, or omitted to remove the module.
   * @returns Completion , with no payload.
   */
  deleteModule(
    moduleId: number,
    placement?: ModulePlacementSelector | null,
  ): Observable<void> {
    const params: HttpParams = modulePlacementParams(placement);

    return this.http.delete<void>(API_ENDPOINTS.modules.byId(moduleId), {
      params,
      context: presentedInContext(),
    });
  }

  /**
   * Reads one module's settings.
   *
   * @param moduleId The module whose settings to read.
   * @param placement The placement whose own settings to include, or omitted for the module-scoped
   * settings alone.
   * @returns Both settings maps.
   */
  getModuleSettings(
    moduleId: number,
    placement?: ModulePlacementSelector | null,
  ): Observable<ModuleSettingsBag> {
    const params: HttpParams = modulePlacementParams(placement);

    return this.http
      .get<unknown>(API_ENDPOINTS.modules.settings(moduleId), {
        params,
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MODULE_SETTINGS_RESPONSE, body)));
  }

  /**
   * Replaces one module's settings. `PUT /modules/{moduleId}/settings`, answering `204` with no body.
   *
   * @param moduleId The module whose settings to replace.
   * @param settings The complete settings state, transmitted whole and unfiltered.
   * @param placement The placement whose own settings are being replaced, or omitted.
   * @returns Completion , with no payload.
   */
  updateModuleSettings(
    moduleId: number,
    settings: ModuleSettingsBag,
    placement?: ModulePlacementSelector | null,
  ): Observable<void> {
    const params: HttpParams = modulePlacementParams(placement);

    return this.http.put<void>(API_ENDPOINTS.modules.settings(moduleId), settings, {
      params,
      context: presentedInContext(),
    });
  }

  /**
   * Exports the content held by one module. `POST /modules/{moduleId}/export`, answering **`200` with the
   * exported document in the RESPONSE BODY**.
   *
   * @param moduleId The module to export.
   * @param request The name the caller intends for the payload, and an optional folder.
   * @returns The exported document as text, which may legitimately be empty.
   */
  exportModule(moduleId: number, request: ModuleExportRequest): Observable<string> {
    return this.http
      .post(API_ENDPOINTS.modules.export(moduleId), request, {
        responseType: 'text',
        context: presentedInContext(),
      })
      .pipe(map((document) => decodeResponse(decodeString, document)));
  }

  /**
   * Imports content into a module. `POST /modules/import`, answering `204` with no body.
   *
   * @param request The target module, the document as text, and optional descriptive folder and name
   * members.
   * @returns Completion , with no payload.
   */
  importModule(request: ModuleImportRequest): Observable<void> {
    return this.http.post<void>(API_ENDPOINTS.modules.import(), request, {
      context: presentedInContext(),
    });
  }

  /** @returns Every definition available to the resolved tenant, or an empty list. */
  listModuleDefinitions(): Observable<readonly ModuleDefinition[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.moduleDefinitions.collection(), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MODULE_DEFINITION_LIST_RESPONSE, body)));
  }

  /**
   * Reads one module definition. `GET /module-definitions/{moduleDefinitionId}`, answering `200` with the
   * definition, or `404` when no such definition is visible to the caller.
   *
   * @param moduleDefinitionId The definition to read.
   * @returns The definition.
   */
  getModuleDefinition(moduleDefinitionId: number): Observable<ModuleDefinition> {
    return this.http
      .get<unknown>(API_ENDPOINTS.moduleDefinitions.byId(moduleDefinitionId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MODULE_DEFINITION_RESPONSE, body)));
  }

  /**
   * Reads the module definitions belonging to one deployed module bundle. `GET
   * /module-definitions/desktop-modules/{desktopModuleId}`, answering `200`.
   *
   * @param desktopModuleId The deployed bundle whose definitions to read.
   * @returns That bundle's definitions, or an empty list.
   */
  listDesktopModuleDefinitions(
    desktopModuleId: number,
  ): Observable<readonly ModuleDefinition[]> {
    return this.http
      .get<unknown>(API_ENDPOINTS.moduleDefinitions.forDesktopModule(desktopModuleId), {
        context: presentedInContext(),
      })
      .pipe(map((body) => decodeResponse(MODULE_DEFINITION_LIST_RESPONSE, body)));
  }
}
