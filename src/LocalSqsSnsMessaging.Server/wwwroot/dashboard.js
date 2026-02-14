function dashboard() {
    return {
        view: 'resources',
        state: { queues: [], topics: [], subscriptions: [], recentOperations: [] },
        selected: null,
        selectedType: null,
        detailTab: 'info',
        connected: false,
        messages: null,
        expandedMsg: null,
        activityFilter: 'all',
        _eventSource: null,

        init() {
            this.connectSSE();
            window.addEventListener('resize', () => this.drawConnections());
            new ResizeObserver(() => this.drawConnections()).observe(this.$refs.graphContainer);
        },

        connectSSE() {
            if (this._eventSource) {
                this._eventSource.close();
            }

            this._eventSource = new EventSource('/_ui/api/state/stream');

            this._eventSource.addEventListener('state', (event) => {
                this.connected = true;
                this.applyState(JSON.parse(event.data));
            });

            this._eventSource.onerror = () => {
                this.connected = false;
            };
        },

        applyState(newState) {
            this.state = newState;
            // Update selected resource data if still exists
            if (this.selected) {
                if (this.selectedType === 'queue') {
                    const q = this.state.queues.find(q => q.arn === this.selected.arn);
                    if (q) this.selected = q;
                    else this.selected = null;
                } else {
                    const t = this.state.topics.find(t => t.arn === this.selected.arn);
                    if (t) this.selected = t;
                    else this.selected = null;
                }
            }
            // Auto-refresh messages if on messages tab
            if (this.selectedType === 'queue' && this.detailTab === 'messages') {
                this.loadMessages();
            }
            if (this.view === 'resources') {
                this.$nextTick(() => this.drawConnections());
            }
        },

        selectQueue(queue) {
            this.selected = queue;
            this.selectedType = 'queue';
            this.detailTab = 'info';
            this.expandedMsg = null;
            this.loadMessages();
        },

        selectTopic(topic) {
            this.selected = topic;
            this.selectedType = 'topic';
            this.detailTab = 'info';
            this.messages = null;
            this.expandedMsg = null;
        },

        async loadMessages() {
            if (!this.selected || this.selectedType !== 'queue') return;
            try {
                const resp = await fetch(`/_ui/api/queues/${encodeURIComponent(this.selected.name)}/messages`);
                if (resp.ok) {
                    this.messages = await resp.json();
                }
            } catch(e) {
                this.messages = { pendingMessages: [], inFlightMessages: [] };
            }
        },

        toggleMsg(id) {
            this.expandedMsg = this.expandedMsg === id ? null : id;
        },

        formatBody(body) {
            try {
                return JSON.stringify(JSON.parse(body), null, 2);
            } catch {
                return body;
            }
        },

        copyText(text) {
            navigator.clipboard.writeText(text);
        },

        getTopicSubCount(topic) {
            return this.state.subscriptions.filter(s => s.topicArn === topic.arn).length;
        },

        getTopicSubscriptions(topic) {
            return this.state.subscriptions.filter(s => s.topicArn === topic.arn);
        },

        getQueueSubscriptions(queue) {
            return this.state.subscriptions.filter(s => {
                return s.endpoint === queue.arn || s.endpoint === queue.url;
            });
        },

        getTopicNameFromArn(arn) {
            const parts = arn.split(':');
            return parts[parts.length - 1];
        },

        getQueueNameFromEndpoint(endpoint) {
            if (endpoint.startsWith('arn:')) {
                const parts = endpoint.split(':');
                return parts[parts.length - 1];
            }
            const parts = endpoint.split('/');
            return parts[parts.length - 1];
        },

        isDlqTarget(queue) {
            return this.state.queues.some(q => q.deadLetterQueueName === queue.name);
        },

        filteredOperations() {
            const ops = this.state.recentOperations || [];
            if (this.activityFilter === 'all') return ops;
            return ops.filter(o => o.service === this.activityFilter);
        },

        formatTime(timestamp) {
            const d = new Date(timestamp);
            return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit', fractionalSecondDigits: 1 });
        },

        simplifyArn(arn) {
            if (!arn) return '\u2014';
            const parts = arn.split(':');
            return parts[parts.length - 1];
        },

        drawConnections() {
            const container = this.$refs.graphContainer;
            const svg = this.$refs.svgOverlay;
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

            // Draw subscription lines (topic -> queue)
            for (const sub of this.state.subscriptions) {
                const topicEl = container.querySelector(`[data-arn="${CSS.escape(sub.topicArn)}"]`);
                const queueName = this.getQueueNameFromEndpoint(sub.endpoint);
                const queue = this.state.queues.find(q => q.name === queueName);
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

            // Draw DLQ lines (queue -> DLQ)
            for (const queue of this.state.queues) {
                if (!queue.hasDeadLetterQueue) continue;
                const dlq = this.state.queues.find(q => q.name === queue.deadLetterQueueName);
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
        }
    };
}
