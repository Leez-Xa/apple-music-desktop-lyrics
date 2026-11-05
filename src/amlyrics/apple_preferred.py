"""Connect observed Apple text and explicit external timing without false provenance."""
from __future__ import annotations
from datetime import datetime, timezone
import logging
import time

LOG=logging.getLogger(__name__)
from .apple_lyrics import parse_apple_lyrics_observation, get_apple_timed_result, combine_apple_text_with_timing

class ApplePreferredLookup:
    def __init__(self, apple_monitor, resolver, *, allow_network=False):
        self.apple_monitor=apple_monitor
        self.resolver=resolver
        self.allow_network=allow_network
        self.last_diagnostic=''

    def __call__(self,state):
        started=time.monotonic()
        LOG.info("lyrics_lookup_started title=%r allow_network=%s",state.title,self.allow_network)
        observation=parse_apple_lyrics_observation(self.apple_monitor.latest_snapshot,state)
        if observation is not None:
            age=(datetime.now(timezone.utc)-observation.observed_at).total_seconds()
            if not -2<=age<=10:
                observation=None
        apple=get_apple_timed_result(observation,state)
        if apple is not None:
            return self._finished(apple,'apple_music',started)
        result=self.resolver.lookup(state,allow_network=self.allow_network)
        self.last_diagnostic=self.resolver.last_diagnostic
        if result is not None and observation is not None:
            combined=combine_apple_text_with_timing(observation,result,state)
            if combined is not None:
                return self._finished(combined,combined.source,started)
        return self._finished(result,self.last_diagnostic,started)

    def _finished(self,result,diagnostic,started):
        self.last_diagnostic=diagnostic
        LOG.info("lyrics_lookup_finished diagnostic=%s source=%s lines=%s elapsed_ms=%s",
                 diagnostic,result.source if result else "",len(result.lines) if result else 0,
                 round((time.monotonic()-started)*1000))
        return result
