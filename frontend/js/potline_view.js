var PotLineView = (function(){
  var ROWS = 10, COLS = 20, POT_W = 40, POT_H = 30, GAP = 6, RADIUS = 4;
  var LABEL_LEFT = 50, LABEL_TOP = 28;
  var pots = [];
  var flashPots = {};
  var highlightPot = null;
  var canvas, ctx;
  var animFrame;
  var flashOn = true;
  var onPotClick = null;

  function init(clickHandler){
    onPotClick = clickHandler;
    canvas = document.getElementById('workshop');
    ctx = canvas.getContext('2d');
    initPots();
    resizeCanvas();
    canvas.addEventListener('click', handleCanvasClick);
    window.addEventListener('resize', resizeCanvas);
    render();
  }

  function initPots(){
    pots = [];
    for(var r=0;r<ROWS;r++){
      for(var c=0;c<COLS;c++){
        var idx = r*COLS+c;
        var id = idx+1;
        pots.push({
          id:id, code:'P-'+String(id).padStart(3,'0'),
          row:r, col:c,
          voltage:3.8+Math.random()*0.8,
          temperature:950+Math.random()*30,
          concentration:1.2+Math.random()*2.3,
          effectProb:Math.random()*100,
          status:'normal'
        });
      }
    }
    updateStatus();
  }

  function getConcentrationColor(conc){
    if(conc>=2.5) return '#4CAF50';
    if(conc>=1.8) return '#FFC107';
    if(conc>=1.5) return '#FF9800';
    return '#F44336';
  }

  function getStatusText(conc){
    if(conc>=2.5) return '正常';
    if(conc>=1.8) return '偏低';
    if(conc>=1.5) return '极低';
    return '危险';
  }

  function updateStatus(){
    var normal=0,low=0,veryLow=0,critical=0;
    pots.forEach(function(p){
      if(p.concentration>=2.5){p.status='normal';normal++;}
      else if(p.concentration>=1.8){p.status='low';low++;}
      else if(p.concentration>=1.5){p.status='veryLow';veryLow++;}
      else{p.status='critical';critical++;}
    });
    document.getElementById('statTotal').textContent=pots.length;
    document.getElementById('statNormal').textContent=normal;
    document.getElementById('statLow').textContent=low;
    document.getElementById('statVeryLow').textContent=veryLow;
    document.getElementById('statCritical').textContent=critical;
  }

  function resizeCanvas(){
    var totalW = LABEL_LEFT + COLS*(POT_W+GAP) - GAP + 20;
    var totalH = LABEL_TOP + ROWS*(POT_H+GAP) - GAP + 20;
    canvas.width = totalW;
    canvas.height = totalH;
  }

  function drawRoundedRect(x,y,w,h,r,fill,stroke){
    ctx.beginPath();
    ctx.moveTo(x+r,y);
    ctx.lineTo(x+w-r,y);ctx.quadraticCurveTo(x+w,y,x+w,y+r);
    ctx.lineTo(x+w,y+h-r);ctx.quadraticCurveTo(x+w,y+h,x+w-r,y+h);
    ctx.lineTo(x+r,y+h);ctx.quadraticCurveTo(x,y+h,x,y+h-r);
    ctx.lineTo(x,y+r);ctx.quadraticCurveTo(x,y,x+r,y);
    ctx.closePath();
    if(fill){ctx.fillStyle=fill;ctx.fill();}
    if(stroke){ctx.strokeStyle=stroke;ctx.lineWidth=2;ctx.stroke();}
  }

  function render(){
    ctx.clearRect(0,0,canvas.width,canvas.height);
    flashOn = !flashOn;

    ctx.font='11px Microsoft YaHei';
    ctx.fillStyle='#6677aa';
    ctx.textAlign='center';
    for(var c=0;c<COLS;c++){
      ctx.fillText('Col '+(c+1), LABEL_LEFT+c*(POT_W+GAP)+POT_W/2, 14);
    }

    ctx.textAlign='right';
    ctx.textBaseline='middle';
    for(var r=0;r<ROWS;r++){
      ctx.fillText('Row '+(r+1), LABEL_LEFT-8, LABEL_TOP+r*(POT_H+GAP)+POT_H/2);
    }

    ctx.textAlign='center';
    ctx.textBaseline='middle';
    pots.forEach(function(p){
      var x = LABEL_LEFT + p.col*(POT_W+GAP);
      var y = LABEL_TOP + p.row*(POT_H+GAP);
      var fillColor = getConcentrationColor(p.concentration);
      var isFlash = p.effectProb > 80;
      var isHighlight = highlightPot === p.id;
      var strokeColor = null;

      if(isFlash && flashOn) strokeColor = '#F44336';
      if(isHighlight){
        strokeColor = '#00e5ff';
        ctx.shadowColor='#00e5ff';
        ctx.shadowBlur=12;
      }

      drawRoundedRect(x,y,POT_W,POT_H,RADIUS,fillColor,strokeColor);

      if(isHighlight){ctx.shadowColor='transparent';ctx.shadowBlur=0;}

      ctx.font='bold 9px monospace';
      ctx.fillStyle='rgba(0,0,0,0.7)';
      ctx.fillText(p.code, x+POT_W/2, y+POT_H/2);
    });

    animFrame = requestAnimationFrame(render);
  }

  function getPotAt(mx,my){
    for(var i=0;i<pots.length;i++){
      var p=pots[i];
      var x=LABEL_LEFT+p.col*(POT_W+GAP);
      var y=LABEL_TOP+p.row*(POT_H+GAP);
      if(mx>=x&&mx<=x+POT_W&&my>=y&&my<=y+POT_H) return p;
    }
    return null;
  }

  function handleCanvasClick(e){
    var rect=canvas.getBoundingClientRect();
    var scaleX=canvas.width/rect.width;
    var scaleY=canvas.height/rect.height;
    var mx=(e.clientX-rect.left)*scaleX;
    var my=(e.clientY-rect.top)*scaleY;
    var pot=getPotAt(mx,my);
    if(pot && onPotClick) onPotClick(pot);
  }

  function updatePotsFromStatus(statusList){
    if(!Array.isArray(statusList)) return;
    statusList.forEach(function(d){
      var pot=pots.find(function(p){return p.id===d.potId;});
      if(pot){
        pot.voltage=d.lastVoltage||pot.voltage;
        pot.temperature=d.potTemperature||pot.temperature;
        pot.concentration=d.estimatedConcentration!==undefined?d.estimatedConcentration:pot.concentration;
        pot.effectProb=d.anodeEffectProbability!==undefined?(d.anodeEffectProbability*100):pot.effectProb;
      }
    });
    updateStatus();
  }

  function setHighlight(potId){
    highlightPot=potId;
    setTimeout(function(){highlightPot=null;},3000);
  }

  function getPots(){ return pots; }

  function simulateData(addAlarmCallback){
    setInterval(function(){
      var idx=Math.floor(Math.random()*pots.length);
      var p=pots[idx];
      p.concentration=Math.max(0.8,Math.min(4.0,p.concentration+(Math.random()-0.5)*0.15));
      p.voltage=Math.max(2.5,Math.min(5.5,p.voltage+(Math.random()-0.5)*0.05));
      p.temperature=Math.max(930,Math.min(990,p.temperature+(Math.random()-0.5)*1.5));
      p.effectProb=Math.max(0,Math.min(100,p.effectProb+(Math.random()-0.5)*5));
      updateStatus();

      if(addAlarmCallback){
        if(p.concentration<1.5&&Math.random()>0.7)
          addAlarmCallback({id:Date.now(),level:1,potCode:p.code,alarmType:'浓度报警',message:'氧化铝浓度过低: '+p.concentration.toFixed(2)+'%',time:new Date()});
        if(p.effectProb>80&&Math.random()>0.7)
          addAlarmCallback({id:Date.now()+1,level:2,potCode:p.code,alarmType:'阳极效应预警',message:'阳极效应概率: '+p.effectProb.toFixed(1)+'%',time:new Date()});
      }
    },3000);
  }

  return {
    init:init, getPots:getPots, updatePotsFromStatus:updatePotsFromStatus,
    setHighlight:setHighlight, getStatusText:getStatusText,
    getConcentrationColor:getConcentrationColor, simulateData:simulateData
  };
})();
