type Count = u16;

const fn width(base: usize) -> usize {
    base * 2
}

const WIDTH: usize = width(1);

struct Pair {
    left: Count,
    right: Count,
}

type PairAlias = Pair;

enum Side {
    Left,
    Right,
}

fn choose(pair: PairAlias, side: Side) -> Count {
    match side {
        Side::Left => pair.left,
        Side::Right => pair.right,
    }
}

fn first(values: &[Count]) -> Count {
    values[0]
}

fn main() {
    let values: [Count; WIDTH] = [20, 22];
    let view: &[Count] = &values;
    let call: fn(Count) -> Count = |value| value + 1;
    let pair = PairAlias { left: call(first(view)), right: 22 };
    let answer: (Count, bool) = (choose(pair, Side::Left), true);
}
