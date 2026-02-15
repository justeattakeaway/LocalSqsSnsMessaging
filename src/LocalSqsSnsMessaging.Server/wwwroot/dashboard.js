import { h, render } from 'https://esm.sh/preact@10';
import { useState, useEffect, useRef, useCallback, useMemo } from 'https://esm.sh/preact@10/hooks';
import htm from 'https://esm.sh/htm@3';

const html = htm.bind(h);

// --- Utilities ---

function getTopicSubCount(state, topic) {
    return state.subscriptions.filter(s => s.topicArn === topic.arn).length;
}

function getTopicSubscriptions(state, topic) {
    return state.subscriptions.filter(s => s.topicArn === topic.arn);
}

function getQueueSubscriptions(state, queue) {
    return state.subscriptions.filter(s => s.endpoint === queue.arn || s.endpoint === queue.url);
}

function getTopicNameFromArn(arn) {
    const parts = arn.split(':');
    return parts[parts.length - 1];
}

function getQueueNameFromEndpoint(endpoint) {
    if (endpoint.startsWith('arn:')) {
        const parts = endpoint.split(':');
        return parts[parts.length - 1];
    }
    const parts = endpoint.split('/');
    return parts[parts.length - 1];
}

function isDlqTarget(state, queue) {
    return state.queues.some(q => q.deadLetterQueueName === queue.name);
}

function formatBody(body) {
    try {
        return JSON.stringify(JSON.parse(body), null, 2);
    } catch {
        return body;
    }
}

function formatTime(timestamp) {
    const d = new Date(timestamp);
    return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 1 });
}

function simplifyArn(arn) {
    if (!arn) return '\u2014';
    const parts = arn.split(':');
    return parts[parts.length - 1];
}

// --- SSE Hook ---

function useSSE(account) {
    const [state, setState] = useState({ accounts: [], currentAccount: '', queues: [], topics: [], subscriptions: [], recentOperations: [] });
    const [connected, setConnected] = useState(false);

    useEffect(() => {
        const params = account ? `?account=${encodeURIComponent(account)}` : '';
        const es = new EventSource(`/_ui/api/state/stream${params}`);
        es.addEventListener('state', (event) => {
            setConnected(true);
            setState(JSON.parse(event.data));
        });
        es.onerror = () => setConnected(false);
        return () => es.close();
    }, [account]);

    return { state, connected };
}

// --- Components ---

function App() {
    const [currentAccount, setCurrentAccount] = useState(null);
    const { state, connected } = useSSE(currentAccount);
    const [view, setView] = useState('resources');
    const [selectedArn, setSelectedArn] = useState(null);
    const [selectedType, setSelectedType] = useState(null);

    const selected = useMemo(() => {
        if (!selectedArn) return null;
        if (selectedType === 'queue') return state.queues.find(q => q.arn === selectedArn) || null;
        if (selectedType === 'topic') return state.topics.find(t => t.arn === selectedArn) || null;
        return null;
    }, [selectedArn, selectedType, state]);

    useEffect(() => {
        if (selectedArn && !selected) {
            setSelectedArn(null);
            setSelectedType(null);
        }
    }, [selectedArn, selected]);

    const selectQueue = useCallback((queue) => {
        setSelectedArn(queue.arn);
        setSelectedType('queue');
    }, []);

    const selectTopic = useCallback((topic) => {
        setSelectedArn(topic.arn);
        setSelectedType('topic');
    }, []);

    const handleAccountChange = useCallback((account) => {
        setCurrentAccount(account);
        setSelectedArn(null);
        setSelectedType(null);
    }, []);

    return html`
        <${TopBar} view=${view} setView=${setView} state=${state} connected=${connected}
            currentAccount=${currentAccount} onAccountChange=${handleAccountChange} />
        ${view === 'resources' && html`
            <${ResourcesView} state=${state} selected=${selected} selectedType=${selectedType}
                selectQueue=${selectQueue} selectTopic=${selectTopic} />
        `}
        ${view === 'activity' && html`
            <${ActivityView} state=${state} />
        `}
    `;
}

function TopBar({ view, setView, state, connected, currentAccount, onAccountChange }) {
    const accounts = state.accounts || [];
    const displayAccount = currentAccount || state.currentAccount || '';
    const opCount = (state.recentOperations || []).length;

    return html`
        <div class="topbar">
            <div class="topbar-left">
                <div class="topbar-title">LocalSqsSnsMessaging <span>Dashboard</span></div>
                <div class="topbar-nav">
                    <button class="nav-btn ${view === 'resources' ? 'active' : ''}"
                        onClick=${() => setView('resources')}>Resources</button>
                    <button class="nav-btn ${view === 'activity' ? 'active' : ''}"
                        onClick=${() => setView('activity')}>
                        Activity${opCount > 0 ? html` <span style="margin-left: 2px; opacity: 0.6;">(${opCount})</span>` : ''}
                    </button>
                </div>
                <div class="stats">
                    <span><span class="stat-value">${state.topics.length}</span> topics</span>
                    <span><span class="stat-value">${state.queues.length}</span> queues</span>
                    <span><span class="stat-value">${state.subscriptions.length}</span> subs</span>
                </div>
            </div>
            <div class="topbar-controls">
                ${accounts.length > 1 && html`
                    <select class="account-select"
                        value=${displayAccount}
                        onChange=${(e) => onAccountChange(e.target.value || null)}>
                        ${accounts.map(acc => html`
                            <option key=${acc} value=${acc}>${acc}</option>
                        `)}
                    </select>
                `}
                ${accounts.length <= 1 && displayAccount && html`
                    <span class="account-label">${displayAccount}</span>
                `}
                <span class="connection-status ${connected ? 'connected' : ''}">
                    <span class="status-dot"></span>
                    <span>${connected ? 'Live' : 'Connecting...'}</span>
                </span>
            </div>
        </div>
    `;
}

function ResourcesView({ state, selected, selectedType, selectQueue, selectTopic }) {
    return html`
        <div class="main">
            <${GraphPanel} state=${state} selected=${selected}
                selectQueue=${selectQueue} selectTopic=${selectTopic} />
            <${DetailPanel} state=${state} selected=${selected} selectedType=${selectedType} />
        </div>
    `;
}

function GraphPanel({ state, selected, selectQueue, selectTopic }) {
    const containerRef = useRef(null);
    const svgRef = useRef(null);
    const drawFnRef = useRef(null);

    drawFnRef.current = () => {
        const container = containerRef.current;
        const svg = svgRef.current;
        if (!container || !svg) return;

        const containerRect = container.getBoundingClientRect();
        let svgContent = `
            <defs>
                <marker id="arrowhead" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
                    <polygon points="0 0, 8 3, 0 6" class="connection-arrowhead" />
                </marker>
                <marker id="arrowhead-dlq" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
                    <polygon points="0 0, 8 3, 0 6" class="connection-arrowhead dlq-arrow" />
                </marker>
            </defs>`;

        for (const sub of state.subscriptions) {
            const topicEl = container.querySelector(`[data-arn="${CSS.escape(sub.topicArn)}"]`);
            const queueName = getQueueNameFromEndpoint(sub.endpoint);
            const queue = state.queues.find(q => q.name === queueName);
            if (!topicEl || !queue) continue;

            const queueEl = container.querySelector(`[data-arn="${CSS.escape(queue.arn)}"]`);
            if (!queueEl) continue;

            const topicRect = topicEl.getBoundingClientRect();
            const queueRect = queueEl.getBoundingClientRect();

            const x1 = topicRect.right - containerRect.left;
            const y1 = topicRect.top + topicRect.height / 2 - containerRect.top;
            const x2 = queueRect.left - containerRect.left;
            const y2 = queueRect.top + queueRect.height / 2 - containerRect.top;

            const midX = (x1 + x2) / 2;
            svgContent += `<path d="M ${x1} ${y1} C ${midX} ${y1}, ${midX} ${y2}, ${x2} ${y2}" class="connection-line subscription-line" marker-end="url(#arrowhead)" />`;
        }

        for (const queue of state.queues) {
            if (!queue.hasDeadLetterQueue) continue;
            const dlq = state.queues.find(q => q.name === queue.deadLetterQueueName);
            if (!dlq) continue;

            const queueEl = container.querySelector(`[data-arn="${CSS.escape(queue.arn)}"]`);
            const dlqEl = container.querySelector(`[data-arn="${CSS.escape(dlq.arn)}"]`);
            if (!queueEl || !dlqEl) continue;

            const queueRect = queueEl.getBoundingClientRect();
            const dlqRect = dlqEl.getBoundingClientRect();

            const x1 = queueRect.left + queueRect.width / 2 - containerRect.left;
            const y1 = queueRect.bottom - containerRect.top;
            const x2 = dlqRect.left + dlqRect.width / 2 - containerRect.left;
            const y2 = dlqRect.top - containerRect.top;

            const midY = (y1 + y2) / 2;
            svgContent += `<path d="M ${x1} ${y1} C ${x1} ${midY}, ${x2} ${midY}, ${x2} ${y2}" class="connection-line dlq-line" marker-end="url(#arrowhead-dlq)" />`;
        }

        svg.innerHTML = svgContent;
    };

    useEffect(() => {
        const handler = () => drawFnRef.current?.();
        window.addEventListener('resize', handler);
        const ro = new ResizeObserver(handler);
        if (containerRef.current) ro.observe(containerRef.current);
        return () => {
            window.removeEventListener('resize', handler);
            ro.disconnect();
        };
    }, []);

    useEffect(() => { drawFnRef.current?.(); });

    const hasResources = state.topics.length > 0 || state.queues.length > 0;

    return html`
        <div class="graph-panel">
            <div class="graph-container" ref=${containerRef}>
                <svg class="svg-overlay" ref=${svgRef}></svg>
                ${!hasResources && html`
                    <div class="detail-empty" style="min-height: 300px;">
                        No resources yet. Create queues and topics to see them here.
                    </div>
                `}
                ${hasResources && html`
                    <div class="graph-columns">
                        ${state.topics.length > 0 && html`
                            <div class="graph-column">
                                <div class="column-header">SNS Topics</div>
                                ${state.topics.map(topic => html`
                                    <${ResourceCard} key=${topic.arn} type="topic" resource=${topic}
                                        selected=${selected?.arn === topic.arn}
                                        isDlq=${false} state=${state}
                                        onClick=${() => selectTopic(topic)} />
                                `)}
                            </div>
                        `}
                        ${state.queues.length > 0 && html`
                            <div class="graph-column">
                                <div class="column-header">SQS Queues</div>
                                ${state.queues.map(queue => html`
                                    <${ResourceCard} key=${queue.arn} type="queue" resource=${queue}
                                        selected=${selected?.arn === queue.arn}
                                        isDlq=${isDlqTarget(state, queue)} state=${state}
                                        onClick=${() => selectQueue(queue)} />
                                `)}
                            </div>
                        `}
                    </div>
                `}
            </div>
        </div>
    `;
}

function ResourceCard({ type, resource, selected, isDlq, state, onClick }) {
    const cls = `resource-card ${type}${selected ? ' selected' : ''}${isDlq ? ' is-dlq' : ''}`;
    return html`
        <div class=${cls} data-arn=${resource.arn} onClick=${onClick}>
            <div class="card-name">${resource.name}</div>
            <div class="card-type">${type === 'topic' ? 'SNS Topic' : (isDlq ? 'Dead Letter Queue' : 'SQS Queue')}</div>
            <div class="card-badges">
                ${type === 'topic' && html`
                    <span class="badge subs-count">${getTopicSubCount(state, resource)} sub${getTopicSubCount(state, resource) !== 1 ? 's' : ''}</span>
                `}
                ${type === 'queue' && html`
                    <span class="badge available">${resource.messagesAvailable} msgs</span>
                    ${resource.messagesInFlight > 0 && html`<span class="badge in-flight">${resource.messagesInFlight} in-flight</span>`}
                    ${resource.isFifo && html`<span class="badge fifo">FIFO</span>`}
                    ${resource.hasDeadLetterQueue && html`<span class="badge dlq">DLQ</span>`}
                `}
            </div>
        </div>
    `;
}

function DetailPanel({ state, selected, selectedType }) {
    if (!selected) {
        return html`
            <div class="detail-panel">
                <div class="detail-empty">Select a resource to view details</div>
            </div>
        `;
    }

    return html`
        <div class="detail-panel">
            <div style="display: flex; flex-direction: column; height: 100%;">
                <div class="detail-header">
                    <div class="detail-title">${selected.name}</div>
                    <div class="detail-subtitle">${selected.arn}</div>
                </div>
                ${selectedType === 'queue'
                    ? html`<${QueueDetail} key=${selected.arn} state=${state} queue=${selected} />`
                    : html`<${TopicDetail} key=${selected.arn} state=${state} topic=${selected} />`
                }
            </div>
        </div>
    `;
}

function QueueDetail({ state, queue }) {
    const [detailTab, setDetailTab] = useState('info');
    const [messages, setMessages] = useState(null);
    const [expandedMsg, setExpandedMsg] = useState(null);

    const loadMessages = useCallback(async () => {
        try {
            const accountParam = state.currentAccount ? `?account=${encodeURIComponent(state.currentAccount)}` : '';
            const resp = await fetch(`/_ui/api/queues/${encodeURIComponent(queue.name)}/messages${accountParam}`);
            if (resp.ok) setMessages(await resp.json());
        } catch {
            setMessages({ pendingMessages: [], inFlightMessages: [] });
        }
    }, [queue.name, state.currentAccount]);

    useEffect(() => { loadMessages(); }, [loadMessages]);

    // Auto-refresh when SSE state changes and on messages tab
    useEffect(() => {
        if (detailTab === 'messages') loadMessages();
    }, [state]);

    const toggleMsg = useCallback((id) => {
        setExpandedMsg(prev => prev === id ? null : id);
    }, []);

    const subs = getQueueSubscriptions(state, queue);

    return html`
        <div style="display: flex; flex-direction: column; flex: 1; overflow: hidden;">
            <div class="detail-tabs">
                <button class="detail-tab ${detailTab === 'info' ? 'active' : ''}"
                    onClick=${() => setDetailTab('info')}>Info</button>
                <button class="detail-tab ${detailTab === 'messages' ? 'active' : ''}"
                    onClick=${() => { setDetailTab('messages'); loadMessages(); }}>
                    Messages${(queue.messagesAvailable + queue.messagesInFlight) > 0
                        ? html` <span style="margin-left: 2px;">(${queue.messagesAvailable + queue.messagesInFlight})</span>` : ''}
                </button>
                <button class="detail-tab ${detailTab === 'subscriptions' ? 'active' : ''}"
                    onClick=${() => setDetailTab('subscriptions')}>Subscriptions</button>
            </div>
            <div class="detail-body">
                ${detailTab === 'info' && html`
                    <table class="props-table">
                        <tr><td>URL</td><td>${queue.url}</td></tr>
                        <tr><td>ARN</td><td>${queue.arn}</td></tr>
                        <tr><td>Type</td><td>${queue.isFifo ? 'FIFO' : 'Standard'}</td></tr>
                        <tr><td>Messages</td><td>${queue.messagesAvailable}</td></tr>
                        <tr><td>In Flight</td><td>${queue.messagesInFlight}</td></tr>
                        <tr><td>Visibility</td><td>${queue.visibilityTimeoutSeconds}s</td></tr>
                        ${queue.hasDeadLetterQueue && html`<tr><td>DLQ</td><td>${queue.deadLetterQueueName}</td></tr>`}
                        ${queue.maxReceiveCount && html`<tr><td>Max Receives</td><td>${queue.maxReceiveCount}</td></tr>`}
                    </table>
                `}
                ${detailTab === 'messages' && html`
                    ${messages === null && html`<div class="no-messages">Loading...</div>`}
                    ${messages !== null && messages.pendingMessages.length === 0 && messages.inFlightMessages.length === 0 && html`
                        <div class="no-messages">No messages in queue</div>
                    `}
                    ${messages !== null && (messages.pendingMessages.length > 0 || messages.inFlightMessages.length > 0) && html`
                        <div>
                            ${messages.pendingMessages.map(msg => html`
                                <${MessageItem} key=${msg.messageId} msg=${msg} status="pending"
                                    expanded=${expandedMsg === msg.messageId} onToggle=${toggleMsg} />
                            `)}
                            ${messages.inFlightMessages.map(msg => html`
                                <${MessageItem} key=${msg.messageId} msg=${msg} status="in-flight"
                                    expanded=${expandedMsg === msg.messageId} onToggle=${toggleMsg} />
                            `)}
                        </div>
                    `}
                `}
                ${detailTab === 'subscriptions' && html`
                    ${subs.length === 0 && html`<div class="no-messages">No subscriptions to this queue</div>`}
                    ${subs.map(sub => html`
                        <${SubscriptionItem} key=${sub.subscriptionArn} sub=${sub} nameFrom="topic" />
                    `)}
                `}
            </div>
        </div>
    `;
}

function TopicDetail({ state, topic }) {
    const [detailTab, setDetailTab] = useState('info');
    const subs = getTopicSubscriptions(state, topic);

    return html`
        <div style="display: flex; flex-direction: column; flex: 1; overflow: hidden;">
            <div class="detail-tabs">
                <button class="detail-tab ${detailTab === 'info' ? 'active' : ''}"
                    onClick=${() => setDetailTab('info')}>Info</button>
                <button class="detail-tab ${detailTab === 'subscriptions' ? 'active' : ''}"
                    onClick=${() => setDetailTab('subscriptions')}>
                    Subscriptions <span style="margin-left: 2px;">(${subs.length})</span>
                </button>
            </div>
            <div class="detail-body">
                ${detailTab === 'info' && html`
                    <table class="props-table">
                        <tr><td>ARN</td><td>${topic.arn}</td></tr>
                        <tr><td>Subscriptions</td><td>${subs.length}</td></tr>
                    </table>
                `}
                ${detailTab === 'subscriptions' && html`
                    ${subs.length === 0 && html`<div class="no-messages">No subscriptions from this topic</div>`}
                    ${subs.map(sub => html`
                        <${SubscriptionItem} key=${sub.subscriptionArn} sub=${sub} nameFrom="queue" />
                    `)}
                `}
            </div>
        </div>
    `;
}

function MessageItem({ msg, status, expanded, onToggle }) {
    return html`
        <div class="message-item ${expanded ? 'expanded' : ''}" onClick=${() => onToggle(msg.messageId)}>
            <div class="message-header">
                <span class="message-id" title=${msg.messageId}>${(msg.messageId || '').substring(0, 8)}...</span>
                <span class="message-status ${status}">${status}</span>
            </div>
            ${!expanded && html`
                <div class="msg-preview">${msg.body.substring(0, 120)}${msg.body.length > 120 ? '...' : ''}</div>
            `}
            ${expanded && html`
                <div>
                    <div class="message-body-container">
                        <div class="message-body">${formatBody(msg.body)}</div>
                        <button class="copy-btn" onClick=${(e) => { e.stopPropagation(); navigator.clipboard.writeText(msg.body); }}>Copy</button>
                    </div>
                    ${msg.messageGroupId && html`
                        <div class="message-attrs">
                            <div class="message-attrs-title">Message Group</div>
                            <dd>${msg.messageGroupId}</dd>
                        </div>
                    `}
                    ${msg.messageAttributes && Object.keys(msg.messageAttributes).length > 0 && html`
                        <div class="message-attrs">
                            <div class="message-attrs-title">Message Attributes</div>
                            ${Object.entries(msg.messageAttributes).map(([key, val]) => html`
                                <div key=${key}><dt>${key}</dt><dd>${val}</dd></div>
                            `)}
                        </div>
                    `}
                    ${msg.attributes && Object.keys(msg.attributes).length > 0 && html`
                        <div class="message-attrs">
                            <div class="message-attrs-title">System Attributes</div>
                            ${Object.entries(msg.attributes).map(([key, val]) => html`
                                <div key=${key}><dt>${key}</dt><dd>${val}</dd></div>
                            `)}
                        </div>
                    `}
                </div>
            `}
        </div>
    `;
}

function SubscriptionItem({ sub, nameFrom }) {
    const name = nameFrom === 'topic'
        ? getTopicNameFromArn(sub.topicArn)
        : getQueueNameFromEndpoint(sub.endpoint);

    return html`
        <div class="sub-item">
            <div class="sub-item-target">${name}</div>
            <div class="sub-item-detail">
                <span>${sub.raw ? 'Raw delivery' : 'SNS envelope'}</span>
                ${sub.filterPolicy && html`<span> \u00B7 Filter: <span>${sub.filterPolicy}</span></span>`}
            </div>
        </div>
    `;
}

function ActivityView({ state }) {
    const [activityFilter, setActivityFilter] = useState('all');

    const ops = state.recentOperations || [];
    const filtered = activityFilter === 'all' ? ops : ops.filter(o => o.service === activityFilter);

    return html`
        <div class="main">
            <div class="activity-panel">
                <div class="activity-toolbar">
                    <div class="activity-toolbar-left">
                        <span class="activity-title">API Activity</span>
                        <span class="activity-count">${filtered.length} operations</span>
                    </div>
                    <div class="activity-filters">
                        <button class="filter-btn ${activityFilter === 'all' ? 'active' : ''}"
                            onClick=${() => setActivityFilter('all')}>All</button>
                        <button class="filter-btn ${activityFilter === 'sqs' ? 'active' : ''}"
                            onClick=${() => setActivityFilter('sqs')}>SQS</button>
                        <button class="filter-btn ${activityFilter === 'sns' ? 'active' : ''}"
                            onClick=${() => setActivityFilter('sns')}>SNS</button>
                    </div>
                </div>
                <div class="activity-list">
                    ${filtered.length === 0 && html`
                        <div class="activity-empty">
                            <span>${ops.length === 0
                                ? 'No API operations recorded yet. Usage tracking is enabled.'
                                : 'No operations match the current filter.'}</span>
                        </div>
                    `}
                    ${filtered.length > 0 && html`
                        <table class="activity-table">
                            <thead>
                                <tr>
                                    <th style="width: 90px;">Time</th>
                                    <th style="width: 50px;">Service</th>
                                    <th style="width: 180px;">Action</th>
                                    <th>Resource</th>
                                    <th style="width: 30px;"></th>
                                </tr>
                            </thead>
                            <tbody>
                                ${filtered.map((op, idx) => html`
                                    <tr key=${idx}>
                                        <td><span class="activity-time">${formatTime(op.timestamp)}</span></td>
                                        <td><span class="activity-service ${op.service}">${op.service.toUpperCase()}</span></td>
                                        <td><span class="activity-action">${op.action}</span></td>
                                        <td><span class="activity-resource" title=${op.resourceArn}>${simplifyArn(op.resourceArn)}</span></td>
                                        <td><span class="activity-result ${op.success ? 'success' : 'failure'}">${op.success ? '\u2713' : '\u2717'}</span></td>
                                    </tr>
                                `)}
                            </tbody>
                        </table>
                    `}
                </div>
            </div>
        </div>
    `;
}

render(html`<${App} />`, document.getElementById('app'));
