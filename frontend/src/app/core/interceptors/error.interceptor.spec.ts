import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';

import { apiUrl } from '../config/api-endpoints';
import { NotificationService } from '../services/notification.service';
import { errorInterceptor } from './error.interceptor';

const URL = apiUrl('portals');

describe('errorInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;
  let notifications: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
      ],
    });

    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    controller.verify();
  });

  /** Issues a request, fails it with the supplied body and status, and settles. */
  async function failWith(body: object, status: number, statusText = 'Error'): Promise<void> {
    const pending = firstValueFrom(http.get(URL));

    controller.expectOne(URL).flush(body, { status, statusText });

    await pending.catch(() => undefined);
  }

  /** The messages queued so far, in order. */
  function messages(): readonly string[] {
    return notifications.notifications().map((entry) => entry.message);
  }

  describe('successful responses', () => {
    it('queues nothing and passes the body through untouched', async () => {
      const pending = firstValueFrom(http.get<{ ok: boolean }>(URL));

      controller.expectOne(URL).flush({ ok: true });

      expect(await pending).toEqual({ ok: true });
      expect(notifications.notifications()).toEqual([]);
    });
  });

  describe('re-throwing', () => {
    it('always re-throws, so the caller still knows its request failed', async () => {
      const pending = firstValueFrom(http.get(URL));

      controller
        .expectOne(URL)
        .flush({ title: 'Not Found', status: 404 }, { status: 404, statusText: 'Not Found' });

      // Swallowing the failure would make every caller appear to succeed, so a form
      // would close and a deleted row would disappear even though nothing happened.
      await expectAsync(pending).toBeRejected();
    });
  });

  describe('message selection', () => {
    it('prefers the problem document detail, which describes this occurrence', async () => {
      await failWith(
        {
          type: 'urn:dnnmigration:error:portal.not_found',
          title: 'Not Found',
          status: 404,
          detail: 'Portal 42 does not exist.',
        },
        404,
      );

      expect(messages()).toEqual(['Portal 42 does not exist.']);
    });

    it('falls back to the title when there is no detail', async () => {
      await failWith({ title: 'Not Found', status: 404 }, 404);

      expect(messages()).toEqual(['Not Found']);
    });

    it('falls back to a status-appropriate sentence when the document carries no text', async () => {
      await failWith({ status: 404 }, 404);

      expect(messages()).toEqual(['The requested item could not be found.']);
    });

    it('treats a whitespace-only detail as absent rather than announcing a blank line', async () => {
      await failWith({ title: 'Not Found', status: 404, detail: '   ' }, 404);

      expect(messages()).toEqual(['Not Found']);
    });

    it('reads a problem document delivered as a JSON string, as a proxy may return it', async () => {
      const pending = firstValueFrom(http.get(URL, { responseType: 'text' }));

      controller
        .expectOne(URL)
        .flush('{"title":"Conflict","status":409,"detail":"Already registered."}', {
          status: 409,
          statusText: 'Conflict',
        });

      await pending.catch(() => undefined);

      expect(messages()).toEqual(['Already registered.']);
    });

    it('falls back by status when the body is not JSON at all', async () => {
      // A gateway returning an HTML error page lands here; no part of that page would
      // describe the failure better than the status does.
      const pending = firstValueFrom(http.get(URL, { responseType: 'text' }));

      controller.expectOne(URL).flush('<html><body>Bad Gateway</body></html>', {
        status: 502,
        statusText: 'Bad Gateway',
      });

      await pending.catch(() => undefined);

      expect(messages()).toEqual(['The server could not complete the request. Try again shortly.']);
    });
  });

  describe('status-specific wording', () => {
    it('names the permission condition for a 403', async () => {
      await failWith({ status: 403 }, 403);

      expect(messages()).toEqual(['You do not have permission to perform this action.']);
    });

    it('names the concurrent-edit condition for a 409, and says what to do about it', async () => {
      await failWith({ status: 409 }, 409);

      expect(messages()).toEqual([
        'This item was changed by someone else. Reload it and apply your changes again.',
      ]);
    });

    it('names the rate-limit condition for a 429', async () => {
      await failWith({ status: 429 }, 429);

      expect(messages()).toEqual(['Too many attempts. Wait a moment and try again.']);
    });

    it('uses one sentence for every server failure', async () => {
      await failWith({ status: 503 }, 503);

      expect(messages()).toEqual(['The server could not complete the request. Try again shortly.']);
    });

    it('describes an unreachable server, rather than reciting a status of zero', async () => {
      // The browser's own text names an internal URL and a status of zero, which tells a
      // person nothing they can act on.
      const pending = firstValueFrom(http.get(URL));

      controller.expectOne(URL).error(new ProgressEvent('error'), { status: 0, statusText: '' });

      await pending.catch(() => undefined);

      expect(messages()).toEqual(['The server could not be reached. Check your connection and try again.']);
    });
  });

  describe('failures that are deliberately not announced', () => {
    it('says nothing about a validation failure, because the fields already show it', async () => {
      await failWith(
        {
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: { PageSize: ['Page size must be between 0 and 500.'] },
        },
        400,
      );

      expect(notifications.notifications())
        .withContext('per-field messages belong beside their fields')
        .toEqual([]);
    });

    it('says nothing about a 401, because either a refresh is under way or the sign-in screen is the message', async () => {
      await failWith({ title: 'Unauthorized', status: 401 }, 401);

      expect(notifications.notifications()).toEqual([]);
    });

    it('does announce a 400 that carries no per-field messages, such as a refused domain operation', async () => {
      // The status alone cannot distinguish the two cases, which is why the `errors`
      // member is the marker rather than the status.
      await failWith(
        {
          type: 'urn:dnnmigration:error:role.name_duplicate',
          title: 'Bad Request',
          status: 400,
          detail: 'A role with that name already exists.',
        },
        400,
      );

      expect(messages()).toEqual(['A role with that name already exists.']);
    });
  });

  describe('severity', () => {
    it('queues every announced failure at error severity', async () => {
      await failWith({ status: 500 }, 500);

      expect(notifications.notifications().map((entry) => entry.severity)).toEqual(['error']);
    });
  });
});
