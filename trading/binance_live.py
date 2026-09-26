"""Small, pinned-host USD-M REST client. Mutations require runtime authorization."""
import hashlib
import hmac
import json
import ssl
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, HTTPSHandler, HTTPRedirectHandler, build_opener

BASE = 'https://fapi.binance.com'
PUBLIC = {'/fapi/v1/time', '/fapi/v1/exchangeInfo', '/fapi/v1/klines',
          '/fapi/v1/ticker/bookTicker', '/fapi/v1/premiumIndex'}
PRIVATE = {'/fapi/v3/account', '/fapi/v3/positionRisk', '/fapi/v1/accountConfig',
           '/fapi/v1/symbolConfig', '/fapi/v1/openOrders', '/fapi/v1/openAlgoOrders',
           '/fapi/v1/order', '/fapi/v1/algoOrder', '/fapi/v1/commissionRate',
           '/fapi/v1/userTrades', '/fapi/v1/income'}
WRITES = {('POST', '/fapi/v1/order'), ('DELETE', '/fapi/v1/order'),
          ('POST', '/fapi/v1/algoOrder'), ('DELETE', '/fapi/v1/algoOrder'),
          ('POST', '/fapi/v1/leverage'), ('POST', '/fapi/v1/marginType')}


class BinanceError(Exception):
    def __init__(self, message, code=None, uncertain=False):
        super().__init__(message)
        self.code, self.uncertain = code, uncertain


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Binance:
    def __init__(self, api_key='', api_secret='', opener=None):
        self.key, self.secret = api_key, api_secret
        self.authorized = False
        self.offset = 0
        self.synced = 0
        self.cooldown_until = 0
        self.opener = opener or build_opener(NoRedirect(), HTTPSHandler(context=ssl.create_default_context()))

    def safe_error(self, message):
        value = str(message)
        for secret in (self.key, self.secret):
            if secret:
                value = value.replace(secret, '[redacted]')
        return value[:350]

    def now(self):
        return int(time.time() * 1000 + self.offset)

    def sync(self):
        before = time.time() * 1000
        result = self.call('GET', '/fapi/v1/time')
        after = time.time() * 1000
        if after - before > 4000:
            raise BinanceError('서버 시간 조회가 지연되었습니다. 다시 연결하세요.')
        self.offset = int(result['serverTime']) - (before + after) / 2
        self.synced = time.monotonic()

    def call(self, method, path, params=None):
        public = path in PUBLIC
        if method == 'GET':
            if not public and path not in PRIVATE:
                raise ValueError('unsupported read endpoint')
        elif (method, path) not in WRITES or not self.authorized:
            raise PermissionError('자동매매 시작 전에는 주문과 계정 설정 변경을 허용하지 않습니다.')
        if time.monotonic() < self.cooldown_until:
            raise BinanceError('Binance 요청 제한 대기 중입니다. 신규 진입은 중지됩니다.')
        payload = dict(params or {})
        headers = {'User-Agent': 'Trading-Desktop/1', 'Accept': 'application/json'}
        if not public:
            if not self.key or not self.secret:
                raise BinanceError('API Key와 Secret을 로컬 설정 창에 입력하세요.')
            payload.update(timestamp=self.now(), recvWindow=5000)
            headers['X-MBX-APIKEY'] = self.key
        query = urlencode(payload)
        if not public:
            signature = hmac.new(self.secret.encode(), query.encode(), hashlib.sha256).hexdigest()
            query += '&signature=' + signature
        url = BASE + path
        body = None
        if method == 'POST':
            body = query.encode()
            headers['Content-Type'] = 'application/x-www-form-urlencoded'
        elif query:
            url += '?' + query
        try:
            with self.opener.open(Request(url, data=body, headers=headers, method=method), timeout=6) as response:
                result = json.loads(response.read(8 * 1024 * 1024).decode('utf-8'))
        except HTTPError as error:
            try:
                result = json.loads(error.read(65536).decode('utf-8'))
            except (ValueError, UnicodeError):
                result = {}
            code = result.get('code')
            if error.code in (418, 429):
                try:
                    wait = max(60, float(error.headers.get('Retry-After', '120')))
                except ValueError:
                    wait = 120
                self.cooldown_until = time.monotonic() + wait
            uncertain = method != 'GET' and (error.code >= 500 or error.code == 408 or code in (-1006, -1007, -4111))
            message = self.safe_error(result.get('msg', '요청 오류'))
            raise BinanceError(f'Binance HTTP {error.code}, code {code}: {message}', code, uncertain) from None
        except (URLError, TimeoutError, OSError, ValueError) as error:
            # Never print signed URLs, headers, secrets, or full network exceptions.
            raise BinanceError('Binance 통신 실패 (' + type(error).__name__ + ')', uncertain=method != 'GET') from None
        if isinstance(result, dict) and isinstance(result.get('code'), int) and result['code'] < 0:
            raise BinanceError(self.safe_error(result.get('msg', 'Binance 요청 실패')), result['code'], method != 'GET')
        return result
