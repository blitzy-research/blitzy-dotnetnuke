import { isApiRequest } from './api-endpoints';

describe('isApiRequest', () => {
  const documentUrl = new URL(document.baseURI);

  it('accepts relative API paths at the configured origin', () => {
    expect(isApiRequest('/api/v1')).toBeTrue();
    expect(isApiRequest('/api/v1/')).toBeTrue();
    expect(isApiRequest('/api/v1/portals/0/modules/0')).toBeTrue();
  });

  it('accepts an absolute URL only when its origin exactly matches the configured origin', () => {
    const sameOrigin = new URL('/api/v1/portals/0', documentUrl);
    const differentPort = new URL(sameOrigin.toString());
    differentPort.port = documentUrl.port === '65534' ? '65533' : '65534';

    expect(isApiRequest(sameOrigin.toString())).toBeTrue();
    expect(isApiRequest(differentPort.toString())).toBeFalse();
  });

  it('ignores query strings and fragments when classifying an API path', () => {
    expect(isApiRequest('/api/v1/users?query=/outside#results')).toBeTrue();
    expect(isApiRequest('/outside?next=/api/v1/users#api/v1')).toBeFalse();
  });

  it('rejects a hostile absolute URL that merely contains the configured path', () => {
    expect(isApiRequest('https://attacker.example/api/v1/users')).toBeFalse();
    expect(isApiRequest('https://attacker.example/redirect/api/v1/users')).toBeFalse();
    expect(isApiRequest('https://attacker.example/?next=/api/v1/users')).toBeFalse();
  });

  it('requires a segment boundary after the configured API version', () => {
    expect(isApiRequest('/api/v10/users')).toBeFalse();
    expect(isApiRequest('/api/v1-preview/users')).toBeFalse();
    expect(isApiRequest('/prefix/api/v1/users')).toBeFalse();
  });

  it('fails closed for text that cannot be parsed as a URL', () => {
    expect(isApiRequest('http://[invalid-host')).toBeFalse();
  });
});
