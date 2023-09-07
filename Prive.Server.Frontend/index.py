from multiprocessing import freeze_support
import sanic
import sanic.response
from sanic_discord import Oauth2, exchange_code
from sanic_discord import AccessToken
import asyncio
import aiohttp
import re
from uuid import uuid4

server = sanic.Sanic("Frontend")
oauth2 = Oauth2(
    server,
    client_id = 1120267062200258590,
    client_secret = "0RjTRk-1yHGufRYFGWe5FujtXZ_88tOa",
    redirect_uri="https://fortnite.day/callback",
    # redirect_uri="http://127.0.0.1:8010/callback",
)

DISCORD_URL = "https://discord.gg/uxRmrFngaa"
LAUNCHER_URL = "https://nightly.link/FortniteJP/Prive/workflows/Prive.Launcher/main/Prive.Launcher.zip"
CLIENT_URL = "https://nightly.link/FortniteJP/Prive/workflows/Prive.Client.Native/main/Prive.Client.Native.zip"
DISPLAYNAME_PATTERN = re.compile(r"^[a-zA-Z0-9]{3,16}$")

@server.listener("before_server_start")
def before_server_start(server, loop):
    server.ctx.aiohttp_session = aiohttp.ClientSession(loop=loop)

@server.listener("after_server_stop")
def after_server_stop(server, loop):
    loop.run_until_complete(server.ctx.aiohttp_session.close())
    loop.close()

@server.get("/")
async def Rindex(request: sanic.Request):
    return await sanic.response.file("./index.html")

@server.get("/discord")
async def Rdiscord(request: sanic.Request):
    return sanic.response.redirect(DISCORD_URL)

@server.get("/launcher")
async def Rlauncher(request: sanic.Request):
    return sanic.response.redirect(LAUNCHER_URL)

@server.get("/client")
async def Rclient(request: sanic.Request):
    return sanic.response.redirect(CLIENT_URL)

@server.get("/dll1")
async def Rdll1(request: sanic.Request):
    return sanic.response.text("?")

@server.get("/console")
async def Rconsole(request: sanic.Request):
    return await sanic.response.file("./FortniteConsole.dll")

@server.get("/callback")
@exchange_code(state = "")
async def Rcallback(request: sanic.Request, access_token: AccessToken):
    print(f"access_token: {access_token}")
    response = sanic.response.redirect("/account")
    response.cookies.add_cookie("access_token", access_token.access_token, expires=access_token.expires_in)
    return response

@server.get("/login")
async def Rlogin(request: sanic.Request):
    return sanic.response.redirect(oauth2.get_authorize_url(state = ""))

@server.get("/account")
async def Raccount(request: sanic.Request):
    data = await oauth2.fetch_user(request.cookies["access_token"])
    if data is None:
        return sanic.response.redirect("/login")
    return await sanic.response.file("./account.html")

@server.get("/api/account")
async def Rapi_account(request: sanic.Request):
    data = await oauth2.fetch_user(request.cookies["access_token"])
    if data is None:
        return sanic.response.redirect("/login")
    account = await server.ctx.aiohttp_session.get(f"https://api.fortnite.day/serverapi/discord/{data['id']}")
    if account.status == 404:
        return sanic.response.json({"status": "not linked"}, 404)
    j = await account.json()
    if "_id" in j.keys(): del j["_id"]
    return sanic.response.json(j)

@server.post("/api/account")
async def Rapi_account_post(request: sanic.Request):
    data = await oauth2.fetch_user(request.cookies["access_token"])
    if data is None:
        return sanic.response.redirect("/login")
    account = await server.ctx.aiohttp_session.get(f"https://api.fortnite.day/serverapi/discord/{data['id']}")
    if account.status == 200:
        return sanic.response.text("already created", 409)
    displayName = request.json.get("displayName")
    if DISPLAYNAME_PATTERN.match(displayName) is None:
        return sanic.response.text("invalid display name", 400)
    j = await server.ctx.aiohttp_session.post(f"https://api.fortnite.day/serverapi/createuser", json = {
        "username": displayName,
        "password": str(uuid4())[:6],
        "discord": data["id"]
    })
    if j.status != 204:
        return sanic.response.text("unknown error", 500)
    return sanic.response.text("ok")

# Structure of the data returned by fetch_user:
# {
#     "id": "602829934515322890",
#     "username": "pdf114514",
#     "global_name": "pdf114514",
#     "avatar": "1a9c86b0247dc6600dd7c6c845fd6bb3",
#     "discriminator": "0",
#     "public_flags": 128,
#     "flags": 128,
#     "banner": null,
#     "banner_color": "#B2FF00",
#     "accent_color": 11730688,
#     "locale": "ja",
#     "mfa_enabled": true,
#     "premium_type": 1,
#     "avatar_decoration": null
# }

if __name__ == "__main__":
    freeze_support()
    server.run("0.0.0.0", 8010)