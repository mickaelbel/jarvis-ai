window.jarvisAgent = (() => {
    'use strict';

    let connection = null;
    let dotNetRef = null;

    function notify(method, arg) {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync(method, arg).catch(() => {});
        }
    }

    function connect() {
        if (connection && connection.state === signalR.HubConnectionState.Connected) return Promise.resolve(true);

        if (!connection) {
            connection = new signalR.HubConnectionBuilder()
                .withUrl('/hubs/agent')
                .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                .configureLogging(signalR.LogLevel.Warning)
                .build();

            connection.on('Connected', () => notify('OnConnectionState', 'connected'));
            connection.on('RunUpdated', (dto) => notify('OnRunUpdated', dto));
            connection.on('RunListChanged', (dto) => notify('OnRunListChanged', dto));
            connection.onreconnecting(() => notify('OnConnectionState', 'reconnecting'));
            connection.onreconnected(() => {
                notify('OnConnectionState', 'connected');
                loadRuns();
            });
            connection.onclose(() => notify('OnConnectionState', 'disconnected'));
        }

        return connection.start().catch(() => false);
    }

    function loadRuns() {
        if (!connection || connection.state !== signalR.HubConnectionState.Connected) return Promise.resolve([]);
        return connection.invoke('GetRuns', 30)
            .then((runs) => { notify('OnRunsLoaded', runs); return runs; })
            .catch(() => []);
    }

    function init(ref) {
        dotNetRef = ref;
    }

    return {
        init: init,
        connect: connect,
        loadRuns: loadRuns
    };
})();
