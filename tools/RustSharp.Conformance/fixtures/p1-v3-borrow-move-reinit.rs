// frozen P1 fixture: borrow-move-reinit
struct Leaf { value: i32 }
struct Pair { left: Leaf, right: i32 }
fn main() {
    let mut pair = Pair { left: Leaf { value: 1 }, right: 2 };
    let moved = pair.left;
    pair.left = Leaf { value: 7 };
    println!("{}", moved.value);
    println!("{}", pair.left.value);
    println!("{}", pair.right);
}
