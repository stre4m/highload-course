import groovy.json.JsonOutput
import groovy.json.JsonSlurper
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.net.http.WebSocket
import java.time.Duration
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong

class PostListener implements WebSocket.Listener {
    private final StringBuilder frame = new StringBuilder()
    final CompletableFuture<Map> delivery = new CompletableFuture<>()

    @Override
    void onOpen(WebSocket socket) {
        socket.request(1)
    }

    @Override
    CompletableFuture<Void> onText(WebSocket socket, CharSequence data, boolean last) {
        frame.append(data)
        if (last) {
            delivery.complete([frame: frame.toString(), receivedAt: System.nanoTime()])
        } else {
            socket.request(1)
        }
        return null
    }

    @Override
    CompletableFuture<Void> onClose(WebSocket socket, int status, String reason) {
        delivery.completeExceptionally(new IOException("WebSocket closed before delivery: " + status))
        return null
    }

    @Override
    void onError(WebSocket socket, Throwable error) {
        delivery.completeExceptionally(error)
    }
}

def stateKey = "hw6.delivery.state"
def state = props.get(stateKey)
def request = { String method, String path, Map payload, String token ->
    def builder = HttpRequest.newBuilder(URI.create("http://127.0.0.1:" + state.ports.first() + path))
        .timeout(Duration.ofSeconds(10))
    if (token != null) {
        builder.header("Authorization", "Bearer " + token)
    }
    if (payload != null) {
        builder.header("Content-Type", "application/json")
    }
    builder.method(method, payload == null ? HttpRequest.BodyPublishers.noBody()
        : HttpRequest.BodyPublishers.ofString(JsonOutput.toJson(payload)))
    def response = state.http.send(builder.build(), HttpResponse.BodyHandlers.ofString())
    assert response.statusCode() == 200 : method + " " + path + ": HTTP " + response.statusCode()
    return response.body().isEmpty() ? null : new JsonSlurper().parseText(response.body())
}

switch (Parameters) {
    case "setup":
        def ports = props.getProperty("ports", "5086").split(",").collect { Integer.parseInt(it.trim()) }
        int socketCount = Integer.parseInt(props.getProperty("sockets", "40"))
        assert socketCount > 0 && socketCount % ports.size() == 0
        assert ports.every { it in [5086, 5087] } && ports.unique(false).size() == ports.size()
        state = [
            http: HttpClient.newBuilder().version(HttpClient.Version.HTTP_1_1)
                .connectTimeout(Duration.ofSeconds(10)).build(),
            ports: ports,
            ready: new CountDownLatch(socketCount),
            published: new CompletableFuture<Map>(),
            started: new AtomicLong()
        ]
        def register = { String name ->
            def password = UUID.randomUUID().toString()
            def user = request("POST", "/user/register", [
                first_name: name, second_name: "JMeter", birthdate: "2000-01-01",
                gender: "test", biography: "HW6 fixture", city: "Test", password: password
            ], null)
            def credentials = [id: user.user_id, password: password]
            return [id: user.user_id, token: request("POST", "/login", credentials, null).token]
        }
        state.author = register("Author")
        state.reader = register("Reader")
        request("PUT", "/friend/set/" + state.author.id, null, state.reader.token)
        props.put(stateKey, state)
        break
    case "connect":
        int port = state.ports[ctx.threadNum % state.ports.size()]
        SampleResult.setSampleLabel("WebSocket handshake :" + port)
        def listener = new PostListener()
        def socket = state.http.newWebSocketBuilder().connectTimeout(Duration.ofSeconds(20))
            .header("Authorization", "Bearer " + state.reader.token)
            .buildAsync(URI.create("ws://127.0.0.1:" + port + "/post/feed/posted"), listener)
            .get(20, TimeUnit.SECONDS)
        vars.putObject("listener", listener)
        vars.putObject("socket", socket)
        vars.put("socketPort", port.toString())
        state.ready.countDown()
        break
    case "publish":
        try {
            assert state.ready.await(25, TimeUnit.SECONDS) : "WebSocket connection barrier timed out"
            def text = "JMeter " + UUID.randomUUID()
            state.started.set(System.nanoTime())
            def id = request("POST", "/post/create", [text: text], state.author.token)
            state.published.complete([postId: id, postText: text, author_user_id: state.author.id])
        } catch (Throwable error) {
            state.published.completeExceptionally(error)
            throw error
        }
        break
    case "receive":
        SampleResult.setSampleLabel("WebSocket delivery :" + vars.get("socketPort"))
        try {
            def expected = state.published.get(40, TimeUnit.SECONDS)
            def delivery = vars.getObject("listener").delivery.get(10, TimeUnit.SECONDS)
            assert new JsonSlurper().parseText(delivery.frame) == expected : "Unexpected WebSocket payload"
            SampleResult.setLatency(TimeUnit.NANOSECONDS.toMillis(delivery.receivedAt - state.started.get()))
        } finally {
            vars.getObject("socket").abort()
        }
        break
}
